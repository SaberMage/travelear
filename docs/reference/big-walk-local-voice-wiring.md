# M3 "bodies read" — SelfEcho / LocalVoicePlayer, the VoicePlayer synth chain, and the megaphone state

Sources: ISIL x64 lift (`IsilDump`, resolved call targets), diffable-cs (offsets/RVAs), and float
constants read directly out of `Big Walk\GameAssembly.dll` (image base `0x180000000`, PE section
walk). Every numeric constant below was resolved from the binary unless marked *(inferred)*.
Companion to `big-walk-voice-dsp.md` (remote listener chain); offsets quoted there are not repeated.

Headline findings, before the detail:

1. **`LocalVoicePlayer` is not a Dissonance pipeline at all.** It plays the raw Unity `Microphone`
   AudioClip (Dissonance's `BasicMicrophoneCapture._clip`, exposed as `DissonanceComms.Clip`)
   through a pooled `AudioSourceController`, and keeps `AudioSource.timeSamples` chasing
   `Microphone.GetPosition() - 512` with a 2048-sample drift tolerance. No `SamplePlaybackComponent`,
   no `VoiceCompressor`, no `BiquadFilters`, no makeup gain, no Opus. The only game-side "DSP" is
   the `AudioSourceController` volume chain plus whatever the Cue's mixer group carries.
2. **`SelfEcho` is a self-monitor of the outdoor echo**, not of the dry voice: three
   `LocalVoicePlayer` emitters (centre / left 60° / right 60°, 1 m out from the listener) whose
   `AudioBus` mixer gets `CenterDelay/LeftDelay/RightDelay`, `CenterDecay/LeftDecay/RightDecay` and
   `MasterVol` from the `AudioDynamicReverb` echo raycasts. It is enabled by adding the Dissonance
   token `"echo"` (`PlayerLips.SetOutdoorEcho`), and it syncs `echoAmount` / `outdoorness` to the
   server every 2 s so remote clients can run `EchoRemote`.
3. **`VoicePlayerType.SelfVoice` is silenced by construction**: `OnEnable` appends a
   `BiquadFilters` (Allpass) with `_vol = 0` while the constructor default `_dryWet = 1`, so the
   Burst kernel emits `0*y + 0*x`. `Clean` is the only type with no extra filter; the mod is right to
   use it.
4. **The megaphone is a `Prop` with a `RadioVoiceAssigner`** whose prefab `VoicePlayer.PlayerType ==
   Megaphone`. "Talking into it" is `RadioVoiceAssigner.isBroadcasting` (set by the networked
   `Peck`), and it manifests locally as the Dissonance token `roomName` on `DissonanceComms`
   (`PlayerLips.SetTalkingIntoRadio`). Remote rendering is decided by `RadioVoiceAssigner.OnAssign`
   over the static `allRadios` list, and the switch is hard (provider swap, no crossfade).

---

## 0. Ownership map

```
SelfEcho (MonoBehaviour, singleton Instance)               follows AudioManager.ListenerController
  EmitterCenter / EmitterLeft / EmitterRight : LocalVoicePlayer      (children, localPos (0,0,1), (-.866,0,.5), (.866,0,.5))
  Mixer : AudioMixer                                                  (CenterDelay.. MasterVol live here)
  _reverb : AudioDynamicReverb  = AudioManager.Instance.AudioDynamicReverb (+0x138)
  EchoAmount : EchoData[6]      (Amount, Delay, Decay per 60° sector)
  EchoOn : bool                 -> mirrored into the "echo" Dissonance token via PlayerLips.SetOutdoorEcho

LocalVoicePlayer (MonoBehaviour)                            one per emitter
  Cue : SoundCue                (bus -> mixer group of the pooled AudioSource)
  ASC : AudioSourceController   = AudioPlayHelper.Play(Cue, ..., clipOverride: DissonanceComms.Clip)
  _cachedClip : AudioClip       = the Microphone clip (10 s loop, 48 kHz)
  _voiceState : VoicePlayerState= DissonanceComms._players[_localPlayerName]   (Amplitude for the gate)
  _volume : AudioVolume         = mute * gate * (1 - speechlessness)
  _microphoneCache : LocalVoicePlayerMicrophoneCache (AddComponent in Awake; per-frame Microphone.GetPosition cache)

EchoRemote (per remote player, on the PlayerVoicePlaybackControl prefab)
  VoicePlayerDirect : VoicePlayer + an instantiated "Echo Remote Close" VoicePlayer under SelfEcho
  both fed from the remote's SamplePlaybackComponent; volumes from PlayerNetworking.echoAmount/outdoorness
```

---

## 1. `SelfEcho` (Assembly-CSharp) — RVAs: Start 0x441DD0, OnEnable 0x442010, OnDisable 0x442270, LateUpdate 0x4422C0 (0xBB5 bytes)

### Fields
| off | field |
|---|---|
|0x20/0x28/0x30|`LocalVoicePlayer EmitterCenter / EmitterLeft / EmitterRight`|
|0x38|`AudioMixer Mixer`|
|0x40|`AudioDynamicReverb _reverb`|
|0x48 / 0x4C|`float _updateRemoteTimerEcho / _updateRemoteTimerOutdoor` (ctor: both `2.0`)|
|0x50|`EchoData[] <EchoAmount>` (ctor: `new EchoData[6]`) *(backing field, offset by elimination)*|
|0x58|`bool <EchoOn>` *(backing field)*|
|0x5C|`float _masterVol`|
|0x60 / 0x64|`float _lastSyncedOutdoorsness / _lastSyncedEchoAmount`|
Statics: `Instance@+0`, `_leftDir@+8 = (-0.8660254, 0, 0.5)`, `_rightDir@+20 = (0.8660254, 0, 0.5)`
(`.cctor` writes `0xBF5DB3D0 / 0 / 0x3F000000` and `0x3F5DB3D0 / 0x3F000000`).
Consts: `DELAY_CENTER "CenterDelay"`, `DELAY_LEFT "LeftDelay"`, `DELAY_RIGHT "RightDelay"`,
`DECAY_CENTER "CenterDecay"`, `DECAY_LEFT "LeftDecay"`, `DECAY_RIGHT "RightDecay"`,
`MASTER_VOL "MasterVol"`, `UPDATE_TOLERANCE 0.0001`.
`struct EchoData { float Amount@0; float Delay@4; float Decay@8; }` (12 B).

### `Start()`
```
_reverb = AudioManager.Instance.AudioDynamicReverb;            // AudioManager +0x138
EmitterCenter.transform.localPosition = (0, 0, 1);
EmitterLeft  .transform.localPosition = _leftDir;              // (-0.866, 0, 0.5)  = 60° left, 1 m
EmitterRight .transform.localPosition = _rightDir;             // ( 0.866, 0, 0.5)  = 60° right, 1 m
```

### `OnEnable()` / `OnDisable()` / `BroadcastForRemoteEcho(pc)`
```
OnEnable:  WorldManager.instance.onLocalPlayerCharcterStart -= BroadcastForRemoteEcho; += BroadcastForRemoteEcho;
           if (WorldManager.localPlayerCharacter) { localPlayerCharacter.lips.SetOutdoorEcho(true); EchoOn = true; _lastSyncedOutdoorsness = 0; }
OnDisable: if (WorldManager.localPlayerCharacter) localPlayerCharacter.lips.SetOutdoorEcho(false); EchoOn = false;
BroadcastForRemoteEcho(pc): pc.lips.SetOutdoorEcho(true); EchoOn = true; _lastSyncedOutdoorsness = 0;
```
`PlayerLips.SetOutdoorEcho(bool)` (below, §5) is the **only** thing that turns the echo "on" for the
network: it adds/removes the Dissonance token `"echo"` on `WorldManager.instance.dissonanceComms`
(guarded by `netIdentity.isLocalPlayer`). `PlayerLips.SetGhost(false)` re-adds `"echo"` if
`SelfEcho.Instance.EchoOn` is set (so respawning as "alive" restores it). Nothing in SelfEcho
gates the *local* emitters on that token — they play whenever the component is enabled; the
token only affects which Dissonance broadcast/receipt triggers fire (prefab data, not readable here).

### `LateUpdate()` — the whole per-frame model
```
listener = AudioManager.Instance.ListenerController (+0x108) .transform;
transform.SetPositionAndRotation(listener.position, listener.rotation);    // SelfEcho rides the listener
outdoor  = _reverb.Outdoorness;                                              // AudioDynamicReverb +0x34

// heading -> 60° sector (0..5)
fwd   = transform.rotation * Vector3.forward;                                // quaternion math inlined
ang   = SignedAngle(Vector2.up, (fwd.x, fwd.z));   ang = ang < 0 ? -ang : 360 - ang;
sector = (int)ang / 60;                                                      // magic 0x88888889 >> 5
left   = sector + (sector < 1 ? 5 : -1);   right = sector + (sector > 4 ? -5 : 1);

// refresh EchoAmount[0..5] from the reverb's smoothed echo buckets (see §1.1)
for i in 0..5:  EchoAmount[i] = CalculateEchoAmount(i);

// emitter gains: ScriptableVolume of each emitter's pooled controller
foreach (emitter, amt) in {(Center, EchoAmount[sector].Amount), (Left, EchoAmount[left].Amount), (Right, EchoAmount[right].Amount)}:
    if (emitter.ASC) {
        target = outdoor * amt;
        emitter.ASC.ScriptableVolume = Mathf.Lerp(emitter.ASC.ScriptableVolume, target, clamp01(Time.deltaTime * 2f))
                                       * emitter.Volume.Volume;                 // AudioSourceController +0xB0, AudioVolume +0x14
    }

// mixer floats (all on SelfEcho.Mixer)
Mixer.SetFloat("CenterDelay", EchoAmount[sector].Delay);  "LeftDelay"  <- [left].Delay;  "RightDelay" <- [right].Delay;
Mixer.SetFloat("CenterDecay", EchoAmount[sector].Decay);  "LeftDecay"  <- [left].Decay;  "RightDecay" <- [right].Decay;
h = clamp01(FootstepSound.<static float @+8> / 100f);                        // LocalPlayerHeightOffTerrain (inferred from the static list)
_masterVol = Mathf.Lerp(_masterVol, h * -6f, clamp01(Time.deltaTime * 3f));  // dB: 0 on the ground -> -6 dB at >= 100 m up
Mixer.SetFloat("MasterVol", _masterVol);

// network sync (only when local player + PlayerNetworking exist and NetworkClient.connectState is 1|2)
_updateRemoteTimerEcho -= dt;
if (_updateRemoteTimerEcho <= 0) {
    avg = (Amount[sector] + Amount[left] + Amount[right]) / 3f;               // NOTE: raw Amount, not * outdoor
    if (|avg - _lastSyncedEchoAmount| > 0.0001f) { _lastSyncedEchoAmount = avg; playerNetworking.CmdSetEchoAmount(avg); _updateRemoteTimerEcho = 2f; }
}
_updateRemoteTimerOutdoor -= dt;
if (_updateRemoteTimerOutdoor <= 0) {
    if (|outdoor - _lastSyncedOutdoorsness| > 0.0001f) { _lastSyncedOutdoorsness = outdoor; playerNetworking.CmdSetOutdoorness(outdoor); _updateRemoteTimerOutdoor = 2f; }
}
```
Constants read: `2.0` (`0x40000000` immediates), `360` (`0x183976F88`), `100` (`0x183976EEC`),
`-6` (`0x1839771CC`), `3` (`0x183976B88`), `1e-4` (`0x1839764B8`). The `avg` and `_masterVol` lerp
both use the same `3.0` constant. `PlayerNetworking.echoAmount@0x148`, `.outdoorness@0x14C`.

### 1.1 `CalculateEchoAmount(int i)` — RVA 0x442EC0 and the reverb's echo buckets
```
b = _reverb.<float[] @+0x130>;                       // 6 sectors x 4 buckets, smoothed (see below)
near = b[i*4+0]; mid = b[i*4+1]; far = b[i*4+2];     // *(stride 4 inferred: the lifter dropped the lea; UpdateEcho writes 4 buckets/sector)*
Delay  = (mid + far > 0) ? far / (mid + far) * 800f + 700f : 700f;     // 0x183976FC8 / 0x183976FC0
Decay  = mid * 0.2f + 0.4f;                                            // 0x183976678 / 0x18397672C
Amount = (1f - near) * (mid + far);
```
`AudioDynamicReverb.UpdateEcho` (AudioSystem) fills the buckets: 6 sectors x 10 rays per call
(`RaycastEcho`); each hit is classified into `_echoCounterFlat[i*4 + k]`:
`k=3` if no usable collider / pass-through material / `hit.point.y < -0.9` (`0x183977148`, i.e.
at or below sea level), else `k=0` if `hit.distance < 50` (`0x183976E98`), `k=1` if `< 200`
(`0x183976F38`), else `k=2`. Every 50 iterations `_echoRatioFlat[k] = counter[k] / 500f`
(`0x183976FA8`) and the counters reset; every call the `+0x130` array is
`Lerp(prev, _echoRatioFlat[k], clamp01(Time.deltaTime * 2f))`. So `near/mid/far` are the
smoothed fractions of rays (over ~500) that found a reflector at < 50 m / 50-200 m / > 200 m in that
60° sector. Delay/Decay land on the mixer's per-emitter Echo effect params (700-1500 ms, decay
0.4-0.6 — Unity `AudioEchoFilter`-style units; the mixer asset itself is not readable here).

### Call graph
`Start -> AudioManager.get_Instance`; `OnEnable/OnDisable/BroadcastForRemoteEcho -> PlayerLips.SetOutdoorEcho`;
`LateUpdate -> AudioMixer.SetFloat x7, NetworkBehaviour.SendCommandInternal("PlayerNetworking::CmdSetEchoAmount"/"CmdSetOutdoorness")`.
Readers of `SelfEcho.Instance`: `PlayerLips.SetGhost` (EchoOn), `EchoRemote.Start/Update` (transform parent + `EchoAmount`).

---

## 2. `LocalVoicePlayer` (Assembly-CSharp) — RVAs: Awake 0x434920, Start 0x4349E0, LateUpdate 0x434B30, OnDissonanceStart 0x434EC0, Play 0x435330

### Fields
| off | field |
|---|---|
|0x20|`SoundCue Cue`|
|0x28|`bool NonStop` (never read in the bodies above; *unused by the read paths*)|
|0x29|`bool AmpGating`|
|0x30|`AudioClip _cachedClip`|
|0x38|`AudioVolume _volume` (ctor: 1/1)|
|0x40|`VoicePlayerState _voiceState` (Dissonance)|
|0x48|`int _bufferSize` (`AudioSettings.GetDSPBufferSize(out _bufferSize, out _)` in Awake; never read afterwards)|
|0x4C|`float _gateVol` (ctor: 1)|
|0x50|`PlayerCharacter _player`|
|0x58|`LocalVoicePlayerMicrophoneCache _microphoneCache`|
|0x60|`AudioSourceController <ASC>`|
|0x68|`Action<AudioSourceController> OnPlay`|

### Lifecycle
```
Awake():  AudioSettings.GetDSPBufferSize(out _bufferSize, out _);
          _microphoneCache = gameObject.AddComponent<LocalVoicePlayerMicrophoneCache>();
Start():  Play();
          _player = WorldManager.localPlayerCharacter ?? (subscribe WorldManager.instance.onLocalPlayerCharcterStart += p => _player = p);
Play():   comms = WorldManager.instance.dissonanceComms;
          if (comms._started /*+0x28*/) OnDissonanceStart();
          else { comms.<Action @+0x100> -= OnDissonanceStart; += OnDissonanceStart; }      // an "OnStarted"-style event *(name inferred)*
OnDestroy() == Stop():  ASC?.Stop();
CheckAndRestart(): if (Cue && (!ASC || !ASC.Initialized)) Play();   (ISIL: "Jump target not found" — body is a tail-jump into Play)
```

### `OnDissonanceStart()` — the actual "play"
```
ASC?.Stop();
comms = WorldManager.instance.dissonanceComms;
_voiceState = comms._players /*+0x68 PlayerCollection*/ .<Dictionary @+0x10>.TryGetValue(comms._localPlayerName /*+0x90*/);   // the local VoicePlayerState
_cachedClip = comms.Clip;                        // DissonanceComms.get_Clip: _capture(+0x80)._microphone(+0x40) as BasicMicrophoneCapture -> ._clip(+0x50)
AudioPlayHelper.TempVolumeList.Add(_volume);
ASC = AudioPlayHelper.Play(Cue, worldPosition: 0, owner: this, xProvider: null, followTransform: transform, rtpc: true,
                           delay: 0, fadeInOverride: -1f, clipOverride: _cachedClip, getXFunc: null, editor: false, volumes: <TempVolumeList>);
ASC.AddEventOnStop(this, (p, c) => { if (p.ASC == c) p.ASC = null; });
if (ASC.AudioSource) {
    micPos = MicManager.GetPosition(SettingsWardrobe.instance.<+0x40>.<+0x28> /* selected mic device name */);
    ASC.AudioSource.timeSamples = wrap(micPos - 512, _cachedClip.samples);      // (micPos-512) % samples, negative -> += samples
}
OnPlay?.Invoke(ASC);
```
The mic clip: `BasicMicrophoneCapture.StartCapture` calls
`MicManager.StartRecording(device, loop: true, lengthSec: 10, frequency: <sample rate>)` — a 10 s
looping mono clip at the capture rate (48 kHz default per the `MicManager` signature). The pooled
`AudioSource` therefore plays the *un-processed* microphone signal (before Dissonance's
preprocessing / VAD / Opus), positioned ~512 samples (10.7 ms @ 48 kHz) behind the write head.

### `LateUpdate()`
```
if (_voiceState && Cue && (!ASC || !ASC.Initialized)) Play();               // self-heal after a pooled stop

// A. amplitude gate
if (AmpGating && _voiceState) {
    amp    = _voiceState.Amplitude;                                           // IVoicePlayerState vtable +0x1A0 (interface call)
    target = 1f - (1f - amp)^4;
    _gateVol = target > _gateVol ? target                                     // instant attack
                                 : Mathf.Lerp(_gateVol, target, clamp01(Time.deltaTime * 10f));   // 0x183976D54
} else _gateVol = 1f;

// B. mute + speechlessness
muted = _player ? (_player.lips.playerCharacter.netIdentity.isLocalPlayer ? _player.lips.localIsMuted /*+0x64*/
                                                                          : _player.playerNetworking.isMuted /*+0x142*/) : false;
mute  = muted ? 0f : 1f;
sp    = _player ? _player.speechless._speechlessness /*+0x1C*/ : 0f;
_volume.Volume = mute * _gateVol * (1f - sp);

// C. drift correction against the microphone write head
if (ASC && ASC.AudioSource && _cachedClip) {
    micPos = _microphoneCache.GetMicrophonePosition();                        // cached once per frame
    src    = ASC.AudioSource.timeSamples;
    ahead  = micPos > src ? micPos - src : _cachedClip.samples - src + micPos;   // wrap-around distance
    if (ahead > 2048) ASC.AudioSource.timeSamples = wrap(micPos - 512, _cachedClip.samples);
}
```
`LocalVoicePlayerMicrophoneCache`: `Update()` clears `_wasUpdatedThisFrame`;
`GetMicrophonePosition()` = cached `Microphone.GetPosition(device)` (0 if the device id is -1 or
`Microphone.IsRecording` is false), one icall per frame shared by all three emitters. There is no
buffer, no resampling, no latency compensation beyond the fixed 512-sample lag and the 2048-sample
snap window.

### What game/Unity DSP a `LocalVoicePlayer` path applies (that managed DSP would not)
- `AudioSourceController` volume chain (`CalculateVolume`): `_attenuationVol` (Cue RTPC attenuation
  curve), `_fadeVol`, `_rtpcVol`, `_hibernationVol`, `_audioSettingsVol` (bus settings slider),
  `ScriptableVolume` (SelfEcho drives it per emitter), plus every `IAudioVolume` in `_chainOfVolume`
  (`_volume` here). `ScriptablePitch/Pan/SpatialBlend/Spread` overrides (-2 = "no override").
- Unity spatialisation of the emitter `AudioSource` (spatial blend, rolloff, doppler, spread from
  the Cue), occlusion via the Cue's `AudioOcclusionConfig`, and voice-limiting / hibernation.
- The Cue's `AudioBus.MixerGroup` — for SelfEcho that is the mixer carrying the `CenterDelay`… echo
  effects and `MasterVol`.
- **Nothing else**: no compressor, no SoftClip, no makeup gain, no EQ, no BitCrusher. Because the
  clip is the raw mic loop, the audio also bypasses Dissonance preprocessing (noise suppression /
  AGC / VAD) — this is the game's deliberate "hear yourself" tap, not a replica of what remotes hear.

### Is `LocalVoicePlayer` a better host than a Clean `VoicePlayer` in synthesizer mode?
Trade-offs, from the bodies:
| | `LocalVoicePlayer` (mic clip + timeSamples chase) | `VoicePlayer` Clean + `LocalVoiceProvider` ring (synth mode) |
|---|---|---|
| Input | raw `Microphone` clip (pre-Dissonance) | whatever the provider ring holds (mod-decoded round-trip PCM) |
| Latency | fixed 512 samples behind the mic head; snaps when > 2048 behind | `RecommendedVoiceReadHead = write - 2*bufferLength*numBuffers*ch`; resync on `OnWriteHeadJump` |
| Moving parts | 1 icall/frame (`Microphone.GetPosition`), 1 `timeSamples` write on snap | audio-thread ring copy, `_readHead` masking, `AudioFilterMixer` synth multiply |
| Per-sample DSP on the audio thread | none (Unity plays the clip) | `VoicePlayer.ProcessSamples` assignment + clamp; extra filters only for non-Clean types |
| Suitability for mod PCM | **none** — it can only play an `AudioClip`; feeding it mod samples means writing a streaming `AudioClip` (PCMReaderCallback) or `SetData` into a looping clip, i.e. re-creating a ring anyway | designed for exactly this: the provider ring is the injection point |
| Game DSP gained | identical (both are pooled `AudioSourceController`s under a Cue) | identical |
Conclusion: `LocalVoicePlayer` has fewer moving parts only because it reads Unity's own mic ring; for
a *mod-driven* emitter (decoded round-trip PCM) it offers nothing the Clean `VoicePlayer` path does
not, and it would force a second ring (a streaming `AudioClip`) plus its own drift logic. Keep the
Clean `VoicePlayer` host. Two things worth copying from it: the `AudioVolume` gate formula
(`1 - (1-amp)^4`, attack instant / release `dt*10`) and the mute/speechlessness sources.

---

## 3. `VoicePlayer` (Assembly-CSharp) — synth-mode chain, per type

### Fields (delta to the M1 notes)
`_internalBypass@0x40, _bypass@0x41, Volume@0x48, PlayerType@0x50, _controller@0x58,
_sourcePlayerCharacter@0x60, _speechlessVol@0x68, _muteVol@0x70, <LocalVol>@0x78 *(backing)*,
_cachedClip@0x80, _readHead@0x88, _bitCrusher@0x90, _megaphoneMixer@0x98,
_megaphoneMasterMixer@0xA0, _megaphoneIndex@0xA8, _walkietalkieMixer@0xB0, OnVoicePlayed@0xB8`.
Statics (`.cctor`): `MEGAPHONE_WET@+0 = {"Megaphone1Wet".."Megaphone4Wet"}`,
`MEGAPHONE_DRY@+8 = {"Megaphone1Dry".."Megaphone4Dry"}`.
`enum VoicePlayerType { Clean=0, Radio=1, SelfVoice=2, Megaphone=3, WalkieTalkie=4 }`.

### Audio thread, once per DSP block (synthesizer mode)
```
AudioFilterMixer.OnAudioFilterRead(data, channels)        // AudioSystem, RVA 0x53CB10
  if (SynthesizerMode /*+0x30*/) { _cachedData = copy(data); data[i] = 0; }      // keep Unity's spatialised 1.0-envelope, clear the buffer
  for f in Filters: f.ProcessSamples(ref data, channels)
    [0] VoicePlayer.ProcessSamples  (RVA 0x44CD00):
          provider = LocalVoiceProvider(+0x28) ?? SamplePlaybackComponent(+0x30); if (!provider || _internalBypass || _bypass) return;
          ring = provider.CachedVoiceData;                                        // interface call
          for i in 0..data.Length-1: { _readHead &= ring.Length-1; data[i] = ring[_readHead]; _readHead++; }   // ASSIGN, unity gain, interleaved 1:1
    [1..] type-specific (see table) — Clean adds nothing
  if (SynthesizerMode) data[i] = clamp(_cachedData[i] * data[i], -1, 1)  else data[i] = clamp(data[i], -1, 1)
```
There is no `SamplePlaybackComponent` DSP, no `VoiceCompressor`, no makeup gain and no blindfold
LPF anywhere in a `VoicePlayer` — those all live in `PlayerVoicePlaybackControl`'s chain.

### `OnEnable()` — RVA 0x44B200 (needs `Cue`)
```
TempVolumeList.Add(_speechlessVol); Add(_muteVol); Add(LocalVol);       // whichever are non-null
_controller = AudioPlayHelper.Play(Cue, 0, owner: this, xProvider: null, followTransform: transform, rtpc: true, 0, -1f,
                                   clipOverride: _cachedClip, getXFunc: GetX, editor: false, volumes: TempVolumeList);
_controller.AddEventOnStop(this, clearRef);   _controller.AddVolume(Volume, this);
_controller._filterSynthesizerMode = true;  _controller._filterMixer.SynthesizerMode = true;      // +0x128 / mixer +0x30
_controller.AddFilter(this, 0);                                                                 // List.Insert(0, this)
switch (PlayerType) {
  SelfVoice:                bq = (BiquadFilters)_controller.AddFilter(AudioFilterType.Allpass /*0*/, -1);   // AddComponent on the controller GO, Type = Allpass
                            bq._vol = 0f;                                   // ctor defaults: Type=HighShelf, _q=0.6, _frequency=250, _vol=1, _dryWet=1, _clampLimit=1
                            // => kernel output = y*(_dryWet*_vol) + x*(1-_dryWet) = 0  -> SelfVoice is silent
  Radio|Megaphone|Walkie:   bc = _controller.gameObject.GetComponent<BitCrusher>();             // must pre-exist on the pooled controller prefab
                            if (bc) { _controller._filters.Add(bc); ensure _filterMixer (GetComponent/AddComponent<AudioFilterMixer> on the AudioSource GO; .Filters = _filters; .SynthesizerMode = _filterSynthesizerMode);
                                      _bitCrusher = bc; bc.BitDepth = 24; bc.CrushRate = 4800; bc.DryWet = 0.6f; bc.Smooth = 0.6f; bc.Mono = true; }
                            else _bitCrusher = null;
     Megaphone (also):      _megaphoneMixer       = Cue.Bus.MixerGroup.audioMixer;                            // SoundCue +0x28 -> AudioBus +0x20
                            _megaphoneMasterMixer = _megaphoneMixer.outputAudioMixerGroup.audioMixer;         // icalls 0x183F986E8 / 0x183F98718 *(names inferred)*
                            hp = _controller.gameObject.AddComponent<BiquadFilters>(); hp.Type = HighPass /*2*/; _controller._filters.Add(hp);
                            hp.Frequency = 300f (0x183976F6C); hp.Q = 0.4f (0x18397672C);
     WalkieTalkie (also):   _walkietalkieMixer = Cue.Bus.MixerGroup.audioMixer;
}
Update();
OnVoicePlayed?.Invoke(_controller);
_readHead = provider ? provider.RecommendedVoiceReadHead : 0;
```
`Awake()` (RVA 0x44ADA0): `_cachedClip = AudioClip.Create("Voice Player", lengthSamples, 1,
outputSampleRate, stream: true, <>c.PCMReader)`; `IncreaseClipSoundBankCount`; **if
`PlayerType == Megaphone`: `_megaphoneIndex = int.Parse(Regex.Match(Cue.name, "\d+")) - 1`**
(else -1 on parse failure). So the Cue asset name carries the megaphone channel (1..4).
`Update()` re-runs `Awake(); OnEnable();` whenever `Cue` is alive but `_controller` is dead.

### Mixer group / channel selection (`AudioSourceController` + `AudioPool`)
`SetupAudioSource` -> `AudioPool.GetAudioSource(controller)`: the pool is
`Dictionary<AudioMixerGroup, SourcePoolByMixerGroup> _avaiableSourcesByMixerGroup` keyed by
`controller._bus.MixerGroup` (`_bus` +0x78 = `Cue.Bus`). The pooled `AudioSource` objects are
pre-assigned to their group; the controller never writes `outputAudioMixerGroup`. If that group's
queue is empty it takes the group with the most free sources (**a different mixer group**), else
`StealSource`. There is no `Voice{n}` channel logic in `VoicePlayer`: the `Dry{n}/High{n}/
ReverbFallWet{n}/ReverbBoostWet{n}` floats and the 12 pooled `Voice{n}` groups belong to
`PlayerVoicePlaybackControl` only (see the DSP doc §4). A Clean `VoicePlayer` writes **no** mixer
floats; a Megaphone one writes the 13 floats below on its own mixer plus 2 on the master.

### `Update()` — RVA 0x44BDC0, Megaphone / WalkieTalkie blocks (constants resolved)
Common inputs: `d = _controller.GetX(ListenerDistance)`; `outdoorLocal = AudioDynamicReverb.Outdoorness (+0x34)`;
`roomSize = AudioDynamicReverb.RoomSize (+0x30)` *(offset by property declaration order)*;
`ease(t) = (2 - t) * t` (the `T2` the lifter could not decode is `0x183976A84 = 2.0`).
```
if (PlayerType == Megaphone && _controller) {
  if (_bitCrusher) { t = clamp01((d - 5f) / 295f);  bc.DryWet = t*0.5f + 0.5f;  bc.CrushRate = (int)(t*4000f + 4000f);  bc.Smooth = t*0.5f + 0.5f; }
  if (_megaphoneMixer) {
    if (_sourcePlayerCharacter && _sourcePlayerCharacter.netIdentity.isLocalPlayer) {          // ---- LOCAL holder
      bc.DryWet = 0.5f; bc.CrushRate = 4800; bc.Smooth = 0.5f;
      PostCompressorThreshold = -40;  PostCompressorRelease = 0.125;  PostCompressorGain = 6;
      CompressorThreshold     = -35;  CompressorGain = outdoorLocal*3 + 6;
      ReverbDry  = roomSize * -1000;  ReverbWet = 0;  ReverbDecayTime = roomSize^4 * 1.4 + 0.1;
      ReverbDecayHFRatio = 1 - roomSize*0.5 + outdoorLocal*0.5;  ReverbDensity = 0;
      ReverbLF = -250 - roomSize*750 - outdoorLocal*500;  HPFrequency = roomSize*200 + 500;  LPFrequency = 3500;
      master: MEGAPHONE_DRY[idx] = 0;  MEGAPHONE_WET[idx] = -80;          (only if _megaphoneMasterMixer && idx >= 0)
    } else {                                                                                    // ---- REMOTE holder (or none)
      t100=clamp01(d/100) t150=clamp01(d/150) t300=clamp01(d/300) t400=clamp01(d/400) t500=clamp01(d/500) t600=clamp01(d/600) t800=clamp01(d/800)
      e150=ease(t150) e300=ease(t300) e500=ease(t500) e800=ease(t800);  s600 = 1 - sqrt(1 - t600)
      occ  = AudioUtil.EaseInOut(_controller.GetX(OcclusionLevel), 0.5f, t500*5 + 1)
      prod = _sourcePlayerCharacter ? outdoorLocal * _sourcePlayerCharacter.playerNetworking.outdoorness : 0     // +0x14C
      loud = (1 - (1 - 0.75*prod) * (0.75*occ)) * sqrt(1 - t600);   att = 1 - loud
      PostCompressorThreshold = -15 - att*45;  PostCompressorRelease = 2*att + 0.25;  PostCompressorGain = 0;
      CompressorThreshold = -25 - t500*25;     CompressorGain = t400*15 + 6;
      ReverbDry = e800 * -1350;  ReverbWet = e500*1000 - 1000;  ReverbDecayTime = e500*7 + 2;
      ReverbDecayHFRatio = 0.5 - t100*0.4;  ReverbDensity = e300*40;  ReverbLF = e150 * -1000;
      HPFrequency = e300*500;  LPFrequency = 22000 - t100*19000;
      master: MEGAPHONE_WET[idx] = t600 * -12;  MEGAPHONE_DRY[idx] = 20*log10(max(t500, 1e-4));
    }
  }
}
if (PlayerType == WalkieTalkie && _walkietalkieMixer && WaterDepthData) {
  depth = WaterDepthData.GetDepth(transform.position);  t = clamp01(depth / -0.2f);  t = clamp01(t / ((1-t)*0.5f + t));
  _walkietalkieMixer.SetFloat("WalkieTalkieLP", t * -21820 + 22000);
}
// tail, every type
if (_sourcePlayerCharacter) {
  _speechlessVol.Volume = Lerp(_speechlessVol.Volume, 1 - _sourcePlayerCharacter.speechless._speechlessness, clamp01(dt*5));
  muted = isLocal ? lips.localIsMuted : playerNetworking.isMuted;   _muteVol.Volume = muted ? 0 : 1;
} else { _speechlessVol.Volume = 1; _muteVol.Volume = 1; }
```
All 13 megaphone floats are written on `_megaphoneMixer` = the megaphone Cue's own mixer asset
(one of `GlobalAudioEffects.MegaphoneMixers[]`, 4 of them), every frame, by **every enabled
Megaphone `VoicePlayer` whose Cue maps to that index** — two megaphones on one index fight over
the floats. `GlobalAudioEffects.Update` additionally writes `"MegaphonePitch"` on each
`MegaphoneMixers[i]` (speechlessness pitch deduction), and `"WalkieTalkiePitch"` on
`WalkieTalkieMixer`. `VoicePlayer.GetX` answers only `XAxisType 170` with
`GlobalAudioEffects.Instance.VoiceSptialBlend` (+0xCC *(backing offset inferred)*); every other
axis falls through to the controller's default provider.

### `set_SampleProvider(v)` / `set_SourcePlayerCharacter(pc)`
`set_SampleProvider`: stores `v` as `LocalVoiceProvider` (+0x28) or `SamplePlaybackComponent`
(+0x30), nulls the other, `_readHead = v.RecommendedVoiceReadHead`, and `v.OnWriteHeadJump +=
() => _readHead = RecommendedVoiceReadHead`. No ramp: the next audio block reads from the new ring.
`set_SourcePlayerCharacter`: for `Radio|Megaphone|WalkieTalkie` only, `pc.lips._activeVoicePlayers
.Add(this)` (`HashSet<VoicePlayer>` +0xE8) and removes from the previous one — the lips use it
for audibility bookkeeping (`IsAudibleInAnyWay` readers: `AudibilityDebug`,
`ModerationPlayerCard`, `PlayerLips.UpdateEncountered`).

---

## 4. `RadioVoiceAssigner` (Assembly-CSharp) — the megaphone / radio state machine

### Fields
`roomName@0x20 (ctor: "RadioA")`, `trackedStateSystem@0x28 (TrackedPeckState)`,
`onWhenReceiving@0x30 (Transform)`, `radioDisplay@0x38`, `onStartReceiveing@0x40 /
onEndReceiveing@0x48 (PeckSwitch)`, `textChatSource@0x50`, `feedbackSound@0x58 /
feedbackSimulSound@0x60 (SoundCue)`, **`voicePlayer@0x68 (VoicePlayer)`**,
`_cachedVoiceType@0x70`, `localSpecialVoice@0x74`, `textSentSound@0x78 / textReceiveSound@0x80`,
`localVoiceVol@0x88 (ctor: 1)`, `fullDuplex@0x8C`, `logVerbose@0x8D`,
**`latestBroadcastPlayer@0x90 (PlayerCharacter)`**, `isReceiveing@0x98`,
`isWaitingForMessageEnd@0x99`, `isInDeadZone@0x9A`, `_feedbackSource@0xA0 /
_feedbackSimulSource@0xA8`, **`isBroadcasting@0xB0`**.
Statics: `Action<RadioVoiceAssigner> onChange@+0`, `List<RadioVoiceAssigner> allRadios@+8`.
Where it lives: `Prop.radioVoiceAssigner` (+0xA8) on the megaphone/radio prop;
`PeckLogicRadioListener.thisRadio` (+0x28) on radio logic; the player's hand is
`PlayerCharacter.hands (+0x68) .heldProp (+0x38)`; the prop also has `exclusiveHolder@0x2C0`
and `isInRadioDeadzone@0x2F1`.

### Wiring
```
Awake():   trackedStateSystem.AddEffect(Peck);  _cachedVoiceType = voicePlayer.PlayerType;   // prefab value is the "identity" (Megaphone for the megaphone)
OnEnable(): allRadios ??= new; onChange += OnAssign; allRadios.Add(this);
OnDisable(): onChange -= OnAssign; allRadios.Remove(this);
```
`TrackedPeckState` is the Mirror-replicated "peck" (use-held) state of the prop, so `Peck` runs on
every client with the same `PeckContext { NetworkIdentity playerIdentity@0; NetworkIdentity
propIdentity@8; sbyte compressedState@0x10; int actionNumber@0x14 }` *(replication is the peck
system's job; only the local handling was read)*.

### `Peck(PeckContext ctx)` — RVA 0x484D70
```
if (ctx.compressedState != 0) {                                   // ---- pressed / "on"
    latestBroadcastPlayer = ctx.playerIdentity?.GetComponent<PlayerCharacter>();
    latestBroadcastPlayer.lips.SetTalkingIntoRadio(true, roomName);
    isBroadcasting = true;
    onChange?.Invoke(this);
    if (!isInDeadZone) { textChatSource.Initialize(); relay pending TextChatMessages via BroadcastTextChatOverThisChannel; SetOutput(GetCombinedString()); if (any sent && textSentSound) AudioPlayHelper.Play(textSentSound, ...); }
} else {                                                          // ---- released / "off"
    isWaitingForMessageEnd = true;
    ctx.playerIdentity.GetComponent<PlayerCharacter>().lips.SetTalkingIntoRadio(false, roomName);
}
Update():  if (isWaitingForMessageEnd) {
    stillSpeaking = latestBroadcastPlayer ? latestBroadcastPlayer.lips.GetIsSpeakingInto(roomName) : false;   // warns "no most recent speaking player"
    if (!stillSpeaking) { isWaitingForMessageEnd = false; isBroadcasting = false; onChange?.Invoke(this); }
}
```
`isBroadcasting` therefore stays true from the press until Dissonance stops reporting the player
speaking into `roomName` (`GetIsSpeakingInto` walks `voicePlayerState.Channels` — the
`RemoteChannel` list, interface vtable +0x280 — and compares each channel's target name with
`roomName`). The Dissonance channel metadata, not a game RPC, tells receivers which room a remote
player is speaking into.

### `OnAssign(changed)` — RVA 0x484A00 (runs on every radio, on every `onChange`)
```
if (changed.roomName != roomName) return;                   // string equality (SequenceEqual)
if (isInDeadZone) { StopRecieving(); return; }
n = 0; cand = null;
foreach r in allRadios: if (r.isBroadcasting && !r.isInDeadZone && r.roomName == roomName) { n++; cand ??= r; }
if (n == 1 && (cand != this || fullDuplex)) { StartReceiving(cand); RefreshDisplay(); return; }
StopRecieving();
if (n > 1 && isBroadcasting) PlayFeedbackSimulSound(); else _feedbackSimulSource?.FadeOut(-1);
RefreshDisplay();
```
So the remote side renders a peer through *this* radio's `VoicePlayer` when exactly one radio in
the same `roomName` is broadcasting and it is not this radio (unless `fullDuplex`). For a single
megaphone prop the broadcaster and the receiver are the same object, so **the megaphone prefab
must have `fullDuplex = true` or a second same-room assigner** — read `fullDuplex` at runtime.

### `StartReceiving(broadcaster)` / `StopRecieving()` — the transition (hard switch)
```
StartReceiving(b):
  pc      = b.latestBroadcastPlayer;
  isLocal = pc.lips.playerCharacter.netIdentity.isLocalPlayer;
  provider = isLocal ? WorldManager.instance.localVoiceProvider          // +0x38
                     : pc.lips.playerVoicePlaybackControl.dissonanceSampleProvider;   // +0xE0 -> +0x28
  if (provider == null) { Debug.LogError("could not get voiceProvider"); return; }
  voicePlayer.SampleProvider = provider;                                  // readHead snaps, no ramp
  voicePlayer.LocalVol.Volume = isLocal ? 1f : localVoiceVol;
  voicePlayer.PlayerType = (localSpecialVoice && isLocal) ? SelfVoice : _cachedVoiceType;   // NOTE: PlayerType write only; filters were built in OnEnable
  voicePlayer.SourcePlayerCharacter = pc;
  onWhenReceiving.gameObject.SetActive(true);
  if (!isReceiveing && NetworkServer.active && onStartReceiveing) onStartReceiveing.Peck(ctx{playerIdentity = pc?.netIdentity});
  _feedbackSimulSource?.FadeOut(-1);  PlayFeedbackSound();  isReceiveing = true;
StopRecieving():
  if (!isReceiveing) return;
  voicePlayer.SampleProvider = null;                                       // ProcessSamples returns early -> synth output = 0 next block
  onWhenReceiving.gameObject.SetActive(false);
  if (NetworkServer.active && onEndReceiveing) onEndReceiveing.Peck(ctx{});
  _feedbackSource?.FadeOut(-1);  _feedbackSimulSource?.FadeOut(-1);  isReceiveing = false;
```
No crossfade on the voice: the provider pointer flips between DSP blocks; the only softening is
the `feedbackSound` cue (`AudioManager.Play` at the radio, `PlayFeedbackSound`) on start and the
`FadeOut(-1)` (Cue default fade) of the feedback sources on stop. Because `PlayerType` is written
*after* `OnEnable` built the filter chain, the SelfVoice/Megaphone filter set is whatever the
prefab type was at enable time; `PlayerType` only steers `Update()`'s mixer-float branch.

---

## 5. `PlayerLips` token setters (Assembly-CSharp)

```
SetTalkingIntoRadio(bool isTalking, string room)   // RVA (PlayerLips), logs "setting broadcast token: <room> setting to value: True/False" when logVerbose
  if (!playerCharacter.netIdentity.isLocalPlayer) return;       // PlayerCharacter +0x40 (NetworkBehaviour.netIdentity) -> NetworkIdentity +0x22 (isLocalPlayer)
  comms = WorldManager.instance.dissonanceComms;                // +0x30
  has   = comms._tokens /*TokenSet +0xC0*/ .Find(room) >= 0;
  if (isTalking) { if (has) Debug.LogWarning("already has radio token: " + room); else comms.AddToken(room); }
  else           { if (has) comms.RemoveToken(room);           else Debug.LogWarning("does not have radio token: " + room); }

SetOutdoorEcho(bool on)  — identical shape with the literal token "echo" ("already has echo token." / "does not have echo token").
SetGhost(bool ghost)     — alive: AddToken("alive"), RemoveToken("ghost"), and AddToken("echo") if SelfEcho.Instance && SelfEcho.Instance.EchoOn;
                           ghost: RemoveToken("alive"), AddToken("ghost"), RemoveToken("echo"); also toggles voicePlayerState/2D flags.
```
Neither setter touches `VoiceBroadcastTrigger` (+0x20 on the lips) directly. Dissonance semantics:
a `VoiceBroadcastTrigger` whose token list contains the room name activates when the comms hold
that token, so adding `roomName` starts transmitting into room `roomName` (metadata carried to
receivers as a `RemoteChannel`), and `"echo"` gates whatever trigger/receipt the player prefab
binds to it. The trigger/token bindings are prefab data and were **not** readable from the binary.

---

## 6. `EchoRemote` (remote side of the outdoor echo) — for completeness
`Awake/Start`: `VoicePlayerDirect.SampleProvider = DissonanceSampleProvider`,
`.SourcePlayerCharacter = VoiceControl.playerCharacter`; instantiates `VoicePlayerClosePrefab`
**parented under `SelfEcho.Instance.transform`** at localPosition (0,0,1), names it
"Echo Remote Close", wires the same provider/character, subscribes `OnVoicePlayed` to add
`_echoCloseVol` / `_echoDirectVol` to the controller. `Update`: `_distToLocal2D` = XZ distance
remote->SelfEcho; sector from the *remote's* heading (`kernal` +0x1A8 rotation) but sampled from the
**local** `SelfEcho.EchoAmount`; `_echoDirectVol.Volume = Lerp(v, (Amt[c]+Amt[l]+Amt[r])/3 *
outdoorLocal * remote.playerNetworking.outdoorness, clamp01(dt*3))`;
`_echoCloseVol.Volume = Lerp(v, remote.playerNetworking.echoAmount * outdoorLocal, clamp01(dt*2))`.
This is why SelfEcho syncs `echoAmount`/`outdoorness`.

---

## 7. Answers, condensed

### Q2a — SelfEcho
Owns the three `LocalVoicePlayer` emitters, the `Mixer`, `_reverb`, `EchoAmount[6]`, `EchoOn`,
`_masterVol`, two 2 s sync timers and the last-synced values. Per `LateUpdate` it sets seven
floats on `Mixer`: `CenterDelay/LeftDelay/RightDelay = far/(mid+far)*800+700` (700 when no
reflection), `CenterDecay/LeftDecay/RightDecay = mid*0.2+0.4`, `MasterVol =
lerp(clamp01(heightOffTerrain/100) * -6 dB, rate 3/s)`; and each emitter's controller
`ScriptableVolume = lerp(outdoorness * (1-near)*(mid+far), rate 2/s) * emitter.Volume`. Audio input:
**the raw Unity microphone clip** (`DissonanceComms.Clip` -> `BasicMicrophoneCapture._clip`), not
`LocalVoiceProvider`, not a `IVoiceDataProvider`. Enabled by `OnEnable` ->
`PlayerLips.SetOutdoorEcho(true)` -> `DissonanceComms.AddToken("echo")`; disabled by the mirror.

### Q2b — LocalVoicePlayer
Hosts nothing from Dissonance's playback side: it plays the mic `AudioClip` on a pooled
`AudioSourceController` (`AudioPlayHelper.Play(Cue, ..., clipOverride: clip)`), driven through the
Cue's `AudioBus.MixerGroup`; playhead = `Microphone.GetPosition - 512`, resnapped when the mic head
runs > 2048 samples ahead. `LocalVoicePlayerMicrophoneCache` only memoises
`Microphone.GetPosition` once per frame (no buffers, no sample rate of its own; the clip is the
10 s / 48 kHz loop `MicManager.StartRecording` made). Applied DSP = `AudioSourceController`
volume chain + Unity spatialisation + the Cue's mixer group; no compressor/EQ/makeup. Not a better
host for mod PCM (needs an `AudioClip`); keep the Clean `VoicePlayer`.

### Q2c — constants for a v1.1 cliff-echo re-synthesis
Sector = 60° of heading (6 sectors, centre/±60° emitters at 1 m); bucket thresholds 50 m / 200 m,
ground/sea rejection `y < -0.9`, 10 rays x 6 sectors per call, ratios over 500 rays, smoothed at
`dt*2`. `Delay(ms) = 700 + 800 * far/(mid+far)`, `Decay = 0.4 + 0.2*mid`, `Amount =
(1-near)*(mid+far)`, emitter gain `outdoorness * Amount` slewed at `dt*2`, `MasterVol` 0 -> -6 dB
with height 0 -> 100 m slewed at `dt*3`. Mixer names: `CenterDelay LeftDelay RightDelay
CenterDecay LeftDecay RightDecay MasterVol` on `SelfEcho.Mixer`.

### Q1 support — VoicePlayer synth chain
Between the ring read and the AudioSource output: `VoicePlayer.ProcessSamples` (assign
`ring[readHead++]`), then only the type-specific filters (`Clean`: none; `SelfVoice`: Allpass
biquad with `_vol=0` = mute; `Radio/Walkie`: `BitCrusher` 24-bit/4800 Hz/0.6 if the pooled
controller carries one; `Megaphone`: `BitCrusher` + `BiquadFilters HighPass 300 Hz Q 0.4`), then
`AudioFilterMixer`'s `clamp(cached_envelope * data, -1, 1)`. Mixer group = `Cue.Bus.MixerGroup`
via the pool dictionary (fallback: the fullest other pool). Per-channel floats: none for Clean;
Megaphone writes 13 on `Cue.Bus.MixerGroup.audioMixer` (`ReverbDry ReverbWet ReverbDensity
ReverbDecayTime ReverbLF ReverbDecayHFRatio HPFrequency LPFrequency CompressorGain
CompressorThreshold PostCompressorThreshold PostCompressorRelease PostCompressorGain`) and
`Megaphone{idx+1}Wet/Dry` on that mixer's parent mixer, all from `VoicePlayer.Update` (§3), not
from `PlayerVoicePlaybackControl` or `GlobalAudioEffects` (which only adds `MegaphonePitch`).

### Q3 — megaphone detection recipe (main thread)
```
var me   = WorldManager.localPlayerCharacter;                       // static
var prop = me?.hands?.heldProp;                                     // PlayerHands +0x38
var rva  = prop?.radioVoiceAssigner;                                // Prop +0xA8
bool holdingMegaphone = rva != null && rva.voicePlayer != null
                     && rva.voicePlayer.PlayerType == VoicePlayer.VoicePlayerType.Megaphone;   // read BEFORE StartReceiving may flip it to SelfVoice; _cachedVoiceType (+0x70, private) is the stable copy
bool talkingIntoIt    = holdingMegaphone && rva.isBroadcasting && rva.latestBroadcastPlayer == me;
// cross-check / alternative: WorldManager.instance.dissonanceComms.HasToken(rva.roomName)  (TokenSet.Find >= 0)
// edge-triggered: subscribe RadioVoiceAssigner.onChange (static) — fires on press, and on release once GetIsSpeakingInto(roomName) drops.
```
Room name: `RadioVoiceAssigner.roomName` on the held prop (serialised; default `"RadioA"`; the
megaphone's actual string is prefab data — there is no `"Megaphone..."` room literal in the code,
only the mixer param names). Transition behaviour: hard switch — `SampleProvider` swap on press
(readHead snapped), `SampleProvider = null` on message end; only the feedback cue fades. If the mod
wants a "megaphone voice" self-render it should start on `isBroadcasting` rising edge and stop on
the falling edge from `onChange`, matching the game's own timing.

### Unreadable / inferred
- Prefab data: the megaphone `roomName`, `fullDuplex`, `localSpecialVoice`, the Cue names
  ("Megaphone1..4"), the mixer assets and which Dissonance triggers bind the `"echo"` / room tokens.
- `AudioDynamicReverb` +0x130 (smoothed echo buckets) and +0x30/+0x34 (RoomSize/Outdoorness) are
  auto-property backing fields placed by declaration order; the SelfEcho index stride (4) is
  inferred from `UpdateEcho`'s four counters per sector (the lifter dropped the `lea`).
- `DissonanceComms` +0x100 event used by `LocalVoicePlayer.Play` (an "OnStarted"-style `Action`)
  and the icall pointers for `AudioMixer.outputAudioMixerGroup` / `AudioMixerGroup.audioMixer`
  are identified by call shape, not by name.

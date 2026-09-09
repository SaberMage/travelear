# M3 "bodies read" — the environment reverb (AudioDynamicReverb, the mixer buses, and what a listener hears on a nearby voice)

Sources: ISIL x64 lift (`IsilDump`, resolved call targets), diffable-cs (offsets/RVAs), float
constants read directly out of `Big Walk\GameAssembly.dll` (image base `0x180000000`, PE section
walk), and — new for this note — the **mixer assets themselves**, read out of
`Big Walk_Data\data.unity3d` with UnityPy 1.25 (`AudioMixerController.m_MixerConstant`: group tree,
effect chains, send targets, exposed-parameter CRC32 names, snapshot defaults). Every numeric
constant below was resolved from the binary or the asset unless marked *(inferred)*. Companion to
`big-walk-voice-dsp.md` (per-voice chain) and `big-walk-local-voice-wiring.md` (SelfEcho, VoicePlayer,
megaphone); offsets quoted there are not repeated.

Headline findings, before the detail:

1. **The reverb the operator hears on friends in the hallway is a Unity "SFX Reverb" on the main
   mixer's `Master Wet` return bus**, exposed as `DryLevel Room RoomHF RoomLF DecayTime DecayHFRatio
   Reflections ReflectDelay Reverb ReverbDelay HFReference LFReference Diffusion Density`, and
   written **every LateUpdate by `AudioDynamicReverb.UpdateReverb`** from four listener-side scalars
   (`RoomSize`, `Outdoorness`, `ReverbTime`, `Diffusion`, all 0..1). It is a *listener* effect: every
   remote voice, the megaphone, the walkie-talkie and every spatial SFX bus sends into it. Nothing
   about the speaker enters it. This is not the per-voice `ReverbFallWet{n}` / `ReverbBoostWet{n}`
   sends.
2. **The mod can read every input.** `AudioManager.Instance.AudioDynamicReverb` (+0x138) exposes
   the four scalars and all fourteen `DSP_*` results as public getters; `GlobalAudioEffects.Mixer`
   exposes the same fourteen names to `AudioMixer.GetFloat`. There is no per-remote-player term,
   so unlike `Dry{n}` / `ReverbFallWet{n}` nothing has to be re-derived for the local player: the
   mod evaluates its own DSP from the game's numbers.
3. **Routing at the Self-Ear (voice mixer → main mixer):** direct path `-3 dB` (voice mixer `Dry`)
   then `-6 dB` (send to `VOICE DRY BUS`) to `Master Dry`; wet path `-3 dB` then `0 dB` (send to
   `VOICE WET BUS`) into the `Master Wet` SFX Reverb whose own `DryLevel` is `-150 - 250*RoomSize`
   mB, so the reverb return also carries a second, *louder* dry copy. Effective mono model:
   `out = bus*(10^(-6/20) + 10^(DryLevel/2000)) + reverb(bus)`, `bus = 10^(-3/20) * voice`.
4. **The per-voice sends are pre-fader and feed two different reverbs**: `ReverbFallWet{n}` → the
   voice mixer's `Reverb Fall` return (+3 dB) with a *fixed* SFX Reverb (4 s, listed in §4);
   `ReverbBoostWet{n}` → `Reverb Boost` (0 dB) whose SFX Reverb mirrors the environment reverb's
   thirteen parameters every frame (`GlobalAudioEffects.Update`), fully wet. `High{n}` is the level
   of a **250 Hz high band** (`Highpass Simple`) summed with a fixed 250 Hz low band, not a 3 kHz
   shelf. Both corrections to the T1 approximations; neither changes the Self-Ear values.
5. **`SelfReverb` (the `WET_*` set) is not the environment reverb.** It parametrises the
   `Master Faint Wet` return that only the game's own self-voice monitor (`Self Voice` group,
   `LocalVoicePlayer`) sends into. Ignore it for Local Voice.
6. No Unity `AudioReverbZone` exists in the build; the one `AudioReverbFilter` is disabled, on a
   stage-speaker prop. Reverb zones in this game are the game's own `ReverbZone` triggers, which
   only override the four scalars.

---

## 0. Signal map (from the mixer assets)

Unity mixer semantics used below: a group's effect list is its processing order; a `Send` placed
before `Attenuation` is pre-fader, after it post-fader; a group whose parent sits at `-80 dB` only
reaches the output through its sends. Built-in effect ids are *(inferred from parameter count,
parameter order and where the exposed names land)*: `-2` Attenuation, `-3` Send, `-4` Receive,
`-5` Duck Volume, `6` Echo, `10` ParamEQ, `11` Pitch Shifter, `12` Chorus, `16` Compressor,
`17` SFX Reverb, `18` Lowpass Simple, `22` Highpass Simple, `1000` plugin (Dissonance Echo
Cancellation, on `Master` only). SFX Reverb parameters are stored in Unity's order
`DryLevel Room RoomHF DecayTime DecayHFRatio Reflections ReflectDelay Reverb ReverbDelay Diffusion
Density HFReference RoomLF LFReference` (mB, s, %, Hz as Unity documents them).

### `voice mixer` (asset id 4531; output group = main mixer `Voice`)
```
Master (0 dB)
├─ Reverb Fall   (+3 dB)   Receive · Attenuation · SFX Reverb [FIXED, §4]
├─ Reverb Boost  (0 dB)    Receive · Attenuation · SFX Reverb [DryLevel -10000 fixed; Room..Density EXPOSED, mirrored from the environment reverb]
├─ Dry           (-3 dB)   Attenuation · Pitch Shifter <VoicePitch> (pitch 1.0, FFT 1024, overlap 4)
│   ├─ Low       (0 dB)    Receive · Lowpass Simple 250 Hz
│   └─ High      (0 dB)    Receive · Highpass Simple 250 Hz
└─ Raw           (-80 dB)  ── direct output muted; children reach Master only through sends
    └─ 1 .. 12   (vol = <Dry{n}>, default -80)
         Send → Reverb Boost  level <ReverbBoostWet{n}>   (PRE-fader, default -80)
         Send → Reverb Fall   level <ReverbFallWet{n}>    (PRE-fader, default -80)
         Attenuation
         Send → Low           level 0 dB  fixed           (post-fader)
         Send → High          level <High{n}>             (post-fader, default 0)
```
The twelve `Voice{n}` cues route the pooled sources into `Raw/1..12` (`PlayerVoicePlaybackControl._index`
is parsed from the group name, DSP doc §4). The mod's Clean `VoicePlayer` takes the last
`GlobalAudioEffects.VoiceCues` entry, i.e. group `12`.

### `main mixer` (asset id 156), the parts a voice touches
```
Master (0 dB)   Attenuation · Send→own Duck (0 dB) · Duck Volume <MasterLimiterThreshold>=-3 dB, ratio 10, attack 0, release 0.125 s, make-up 0, knee 20, sidechain 1 · Dissonance Echo Cancellation
├─ BUS GROUP (-80 dB)                       ── buses reach the output only through their sends
│   ├─ VOICE DRY BUS       (vol <Voice_Dry>)      Receive · Attenuation · Send → Master Dry (0 dB)
│   ├─ VOICE WET BUS       (vol <Voice_Wet>)      Receive · Attenuation · Send → Master Wet (0 dB)
│   ├─ VOICE SUPER WET BUS (vol <Voice_SuperWet>) Receive · Attenuation · Sends → Speechlessness, Blindfold, Ending, BlackTower (0 dB each)
│   ├─ SFX DRY / SFX WET / SFX SUPER WET BUS (vol <SFX_Dry/SFX_Wet/SFX_SuperWet>) same shape → Master Dry / Master Wet / Speechlessness+Blindfold+BlackTower
│   └─ MUSIC 2D BUS / MUSIC 3D BUS (<Music3DVol>)
├─ VOICE GROUP (-80 dB)
│   ├─ Voice         (0 dB)  Attenuation · Send→VOICE DRY BUS -6 dB · Send→VOICE WET BUS 0 dB · Send→VOICE SUPER WET BUS 0 dB   ← voice mixer output
│   ├─ Self Voice    (0 dB)  Attenuation · Send→Master Faint Wet 0 dB                                                       ← self voice mixer (LocalVoicePlayer)
│   ├─ Echo          (0 dB)  Attenuation · Send→VOICE DRY BUS 0 dB                                                          ← echo mixer (SelfEcho / EchoRemote)
│   ├─ Megaphone     (0 dB)  Attenuation · Send→VOICE DRY BUS -6 dB · Send→VOICE WET BUS 0 dB                                ← megaphone master
│   └─ Walkie Talkie (0 dB)  Attenuation · Send→VOICE DRY BUS -3 dB · Send→VOICE WET BUS -9 dB                               ← walkietalkie mixer
├─ SFX GROUP (-80 dB)  Foley (dry 0 / wet -3 / super 0), Footsteps (<FootstepDry> / 0 / <FootstepSuperWet>), Prop (-6 / 0 / -9), Mechanism (0/0/0), Foliage Rustle (-3 / 0), Environment HP/LP (0 / 0) …
└─ Master Group (0 dB)  Attenuation · Lowpass Simple <MasterLP> 22000 · ParamEQ 3300 Hz <MasterFreqGain3k> 1.0 · ParamEQ 1000 Hz <MasterFreqGain1k> 1.0
    ├─ Master Dry       (0 dB)            Receive
    ├─ Master Wet       (vol <MasterWet>) Receive · Attenuation · SFX Reverb <DryLevel … Density>     ← THE ENVIRONMENT REVERB (AudioDynamicReverb.UpdateReverb)
    ├─ Master Faint Wet (0 dB)            Receive · Attenuation · SFX Reverb <WET_DryLevel … WET_Density>  ← SelfReverb.Update (self-voice monitor only)
    └─ Master Super Wet (pitch <SuperWetPitch>)  Attenuation · SFX Reverb [fixed: DryLevel -10000, Room 0, RoomHF -2000, Decay 6.8 s, HF ratio 0.15, Reflections -10000, ReflectDelay 0.01, Reverb 0, ReverbDelay 0.1, Diffusion 100, Density 25, HFRef 5000, RoomLF 0, LFRef 250] · Chorus
        ├─ Blindfold (-80, <SuperWet_Blindfold>) · Speechlessness (-80, <SuperWet_Speechlessness>) · Ending (-80, <SuperWet_Ending>) · BlackTower (-80, <SuperWet_BlackTower>)   (all Receive only)
```
Snapshot defaults for the environment reverb (before the first `UpdateReverb`): `DryLevel 0, Room
-10000, RoomHF 0, DecayTime 1, DecayHFRatio 0.5, Reflections -10000, ReflectDelay 0.02, Reverb 0,
ReverbDelay 0.04, Diffusion 100, Density 100, HFReference 5000, RoomLF 0, LFReference 250` (Unity's
"off" preset). The Super Wet children sit at `-80 dB` unless speechlessness / blindfold / ending /
black-tower effects raise them, so in normal play the super-wet path is silent.

---

## 1. `AudioDynamicReverb` (AudioSystem) — RVAs: Initialize 0x5076D0, UpdateReverb 0x508180, RaycastsReverb 0x508600, CalculateReverb 0x508CA0 (0x14AE bytes), GetSideRaycastResult 0x50A150, GetOcclusionForMaterial 0x50A400, EnterReverbZone 0x50A4A0, ExitReverbZone 0x50A560, CalculateReverbOverride 0x50A620, UpdateEcho 0x50A7F0, RaycastEcho 0x50AD10, CalculatePortals 0x50B4D0, CalculateDirectionShifts 0x50B940, .ctor 0x50BD20

### Fields
| off | field | notes |
|---|---|---|
|0x20|`public bool Bypass`|`UpdateReverb` then writes `DryLevel 0`, `Room -10000` and returns|
|0x21|`public bool UsePortals`|ctor: `true`|
|0x24|`LayerMask _layers`|= `DynamicReverbConfig.Layers`|
|0x28|`AudioMixer <Mixer>`|= `DynamicReverbConfig.Mixer` (the **main mixer**: it exposes `DryLevel..Density`)|
|**0x30**|**`float <RoomSize>`**|0..1, ctor-less; `Initialize` sets 0.5|
|**0x34**|**`float <Outdoorness>`**|0..1, `Initialize` 0.5|
|**0x38**|**`float <ReverbTime>`**|0..1 (a reflectivity average, not seconds), `Initialize` 0.5|
|**0x3C**|**`float <Diffusion>`**|0..1, `Initialize` 0.5|
|0x40..0x74|`float <DSP_DryLevel> <DSP_Room> <DSP_RoomHF> <DSP_RoomLF> <DSP_DecayTime> <DSP_DecayHFRatio> <DSP_Reflections> <DSP_ReflectDelay> <DSP_Reverb> <DSP_ReverbDelay> <DSP_HFReference> <DSP_LFReference> <DSP_Diffusion> <DSP_Density>`|the fourteen values last sent to the mixer (public getters, RVAs 0x2F40C0, 0x2F4370, 0x307500, 0x307510, 0x37BE70, 0x317570, 0x502650, 0x502670, 0x502690, 0x5026B0, 0x375860, 0x507440, 0x2F55E0, 0x507470)|
|0x78 / 0x7C / 0x80 / 0x84|`float _reverbTimeOverride / _diffusionOverride / _roomSizeOverride / _outdoornessOverride`|ctor: all `-1` (= no override)|
|0x88 / 0x90 / 0x98|`FixedSizeFloatQueue _collideHits / _collideLength / _collideReflection`|capacity `_size`|
|0xA0|`int _size`|= `CollideInfoSize`|
|0xA4 / 0xA8|`float _detectionRange / _actualRange`|= `DetectionRange` / `ActualRange`|
|0xAC|`float _initialDiffusion`|= `InitialDiffusion` (the Diffusion target outside zones)|
|0xB0|`bool _inReverbZone`| |
|0xB8|`List<ReverbZone> _reverbZones`| |
|0xC0|`AudioMaterialConfig _materialConfig`| |
|0xC8 / 0xD0|`AnimationCurve _roomSizeCurve / _outdoornessCurve`|= config curves (readable at runtime through `AudioManager.AudioConfig.DynamicReverbConfig`)|
|0xD8|`FixedSizeFloatQueue[] _directionBuckets`|9 x 128 (8 x 45° sectors + up)|
|0xE0 / 0xE8 / 0xF0|`float[] <DirectionFills>` (10) / `float[] <PortalFills>` (8) / `float <Portalness>`|occlusion "fill" per sector; feeds `CalculateDirectionShifts`, **not** the reverb|
|0xF8 / 0x100|`Vector2[] <DirectionOffsets> / <DirectionOffsetsSmoothed>`| |
|0x108 / 0x10C / 0x110|`float <SideToTopFillRatio> / <SideFillsAvg> / <SideFillsMultiplied>`| |
|0x118|`List<AudioPortal> _activePortals`| |
|0x120 / 0x128 / 0x130|`int[] _echoCounterFlat` (24) / `float[] _echoRatioFlat` (24) / `float[] <EchoRatioFlat>` (24, smoothed)|the echo buckets of the wiring doc §1.1 (so `+0x130` is the smoothed public array)|
|0x138|`int _echoIteration`| |
|0x140 / 0x148 / 0x150|`Vector3[] <RandomPoints> / <RandomPointsUpwards> / <RandomPointsEcho>` (60)|ray directions|
|0x158 / 0x168 / 0x178|`NativeArray<RaycastCommand> _raycastCommands` / `NativeArray<RaycastHit> _results` / `JobHandle _raycastJobHandle`|side rays (array length at +0x170)|
|0x188 / 0x198 / 0x1A8|the same for the upward rays (length at +0x1A0)|
|0x1B8 / 0x1C8 / 0x1D8|the same for the echo rays|
|0x1E8|`DynamicReverbConfig _config`| |
|0x1F0|`int _currentRaysAmount`| |
|0x1F4 / 0x1F8|`float _detectionAngleLow / _detectionAngleHigh`| |
Consts: `MIN_COMMANDS_PER_JOB 16`, `SIDE_HIT_NUM 8`, `UP_HIT_NUM 8`, `DIRECTION_FILL_LIST_LENGTH 128`.
Nested `SideRaycastResult { bool HasHit@0; bool HasMaterial@1; bool IsNotPassThrough@2; float ClosestDistance@4; float Occlusion@8; Collider ClosestCollider@0x10; PhysicsMaterial ClosestMaterial@0x18 }`.

`DynamicReverbConfig : AudioAsset` (the asset behind it): `Mixer@0x18, Basic@0x20, Layers@0x24,
RaysAmount@0x28, LowQualityRaysAmount@0x2C, CollideInfoSize@0x30, DetectionRange@0x34,
ActualRange@0x38, DetectionAngleLow@0x3C, DetectionAngleHigh@0x40, InitialDiffusion@0x44,
MaterialConfig@0x48, RoomSizeCurve@0x50, OutdoornessCurve@0x58, IsLowQualityLevelSettings@0x60`.
`AudioMaterialConfig`: `Materials@0x18 (AudioMaterial[] { Material@0x10, OcclusionMultiplier@0x18,
ReverbAbsorption@0x1C })`, `VoiceBlockingMaterials@0x20, OceanMaterial@0x28, TerrainMaterials@0x30,
MoreAmbOcclusionMaterials@0x38`, and the lookups built in `OnEnable`:
`VoiceBlockingMaterialsHashset@0x40, OcclusionLookup@0x48, OcclusionLookupForReverb@0x50,
ReverbLookup@0x58, PassThroughMaterials@0x60, TerrainMaterialsHashset@0x68`.

### Lifecycle (`AudioManager`, RVAs Initialize 0x518A90, LateUpdate 0x517CA0)
```
AudioManager.Initialize(config):
  cfg = config.DynamicReverbConfig (+0x40)
  if (cfg && !cfg.Basic) { AudioDynamicReverb = AddComponent<AudioDynamicReverb>(); AudioDynamicReverb.Initialize(cfg); }   // +0x138
  else                   { AudioBasicReverb   = AddComponent<AudioBasicReverb>();   AudioBasicReverb.Initialize(cfg.Mixer); } // +0x140  (§2.3)
AudioManager.LateUpdate():
  ... occlusion ...
  if (AudioDynamicReverb && AudioDynamicReverb.Initialized) { AudioDynamicReverb.UpdateReverb(); AudioDynamicReverb.UpdateEcho(); }
  if (AudioBasicReverb   && AudioBasicReverb.Initialized)   { AudioBasicReverb.UpdateReverb(); }
  ... AudioReferenceManager.AudioLateUpdate, AudioClock alarms ...
```
`AudioManager` property backings used here: `ListenerController@0x108`, `AudioConfig@0x118`,
`AudioDynamicReverb@0x138`, `AudioBasicReverb@0x140`, `PlayerTransform@0x1D8`.

### `Initialize(DynamicReverbConfig)`
Copies `Layers, Mixer, DetectionRange, ActualRange, InitialDiffusion, CollideInfoSize,
DetectionAngleLow/High, MaterialConfig, RoomSizeCurve, OutdoornessCurve`; `CreateReverbArrays(RaysAmount)`
(side + upward point/command/result arrays, 8 hits per command); 60 echo rays; the three collide
queues at `CollideInfoSize`; `_reverbZones = new`; **`RoomSize = Outdoorness = ReverbTime =
Diffusion = 0.5`**; `_directionBuckets[i] = new FixedSizeFloatQueue(128)` for the 9 buckets.

### `RaycastsReverb()` — the geometry
```
origin = AudioManager.Instance.ListenerController.RandomPointsCenter.position      // AudioListenerController +0x70
for i in RandomPoints:          dir = AudioUtil.GenerateRandomPointUpward(_detectionAngleLow, 55°)   // side band
                                _raycastCommands[i] = { origin, dir, distance = _detectionRange, layerMask = _layers }
_raycastJobHandle = RaycastCommand.ScheduleBatch(_raycastCommands, _results, minCommandsPerJob 16, maxHits 8)
for i in RandomPointsUpwards:   dir = AudioUtil.GenerateRandomPointUpward(55°, 90°)                   // upward cap
                                ... same, into _raycastCommandsUpwards / _resultsUpwards
```
Constants `55` (`0x183976E9C`), `90` (`0x183976EE0`). The jobs complete asynchronously; the next
`UpdateReverb` consumes them only when **both** handles report complete, so the model updates at
most once per frame and skips frames while a job is still running.

### `CalculateReverb()` — the listener model (runs when both jobs are done)
```
dt = Time.deltaTime
refl0 = clamp01(RoomSize - Outdoorness)                    // fallback reflectivity for material-less hits

// (1) side rays, 8 hits per direction
for g in 0 .. _results.Length/8 - 1:
    r = GetSideRaycastResult(g*8, PassThroughMaterials)     // closest hit whose material is not pass-through; Occlusion = max OcclusionLookupForReverb over the 8 hits
    if (!r.HasHit || (r.HasMaterial && !r.IsNotPassThrough)):
        _collideHits.Enqueue(0); _collideLength.Enqueue(_actualRange); _collideReflection.Enqueue(0)
    else:
        _collideLength.Enqueue(min(r.ClosestDistance, _actualRange))
        if (r.ClosestCollider is TerrainCollider)          { hits = 0.5; refl = 0 }
        else if (!r.HasMaterial)                            { hits = 1;   refl = refl0 }
        else { hits = TerrainMaterialsHashset.Contains(mat) ? 0.5 : 1
               refl = ReverbLookup.TryGetValue(mat, out a) ? 1 - a : refl0 }     // a = AudioMaterial.ReverbAbsorption (inferred)
        _collideHits.Enqueue(hits); _collideReflection.Enqueue(refl)
    sector = (int)(heading(RandomPoints[g].xz) / 45) in 0..7;  _directionBuckets[sector].Enqueue(r.Occlusion)

// (2) upward rays, same bookkeeping; the up bucket gets max(OcclusionLookup[mat]) over the 8 hits
    ... _directionBuckets[8].Enqueue(maxOcclusion)

CalculatePortals()
DirectionFills[0..7] = bucket averages   (or lerp(DirectionFills, avg * PortalFills, clamp01(dt*(2*Outdoorness^4 + 1))) when UsePortals;
                                          Portalness = lerp(Portalness, max(1 - PortalFills), clamp01(dt)))
SideFillsAvg = sum/8; SideFillsMultiplied = product; DirectionFills[8] = upAvg; DirectionFills[9] = (upAvg + sum)/9; SideToTopFillRatio = SideFillsAvg - upAvg

// (3) the four reverb scalars
hitsAvg    = clamp01(_collideHits.Average)                                   // 1 = every ray finds a hard wall, 0.5 = terrain, 0 = open
lengthNorm = clamp01(_collideLength.Sum / (_size * _actualRange))           // mean ray length as a fraction of ActualRange
reflAvg    = clamp01(_collideReflection.Average)

roomSizeT  = _inReverbZone && _roomSizeOverride    != -1 ? _roomSizeOverride    : _roomSizeCurve.Evaluate(lengthNorm)
outdoorT   = _inReverbZone && _outdoornessOverride != -1 ? _outdoornessOverride : _outdoornessCurve.Evaluate(1 - hitsAvg)
reverbT    = _inReverbZone && _reverbTimeOverride  != -1 ? _reverbTimeOverride  : reflAvg
diffusionT = _inReverbZone && _diffusionOverride   != -1 ? _diffusionOverride   : _initialDiffusion

RoomSize    = lerp(RoomSize,    roomSizeT,  clamp01(dt * 5))     // 0x183976C80 = 5
Outdoorness = lerp(Outdoorness, outdoorT,   clamp01(dt))
ReverbTime  = lerp(ReverbTime,  reverbT,    clamp01(dt))
Diffusion   = lerp(Diffusion,   diffusionT, clamp01(dt))
CalculateDirectionShifts()
```
So the queues are moving averages over the last `CollideInfoSize` ray results (side and up rays
share them); `RoomSize` follows mean free path through a config curve, `Outdoorness` follows the
fraction of rays that escape through a config curve, `ReverbTime` is the mean surface
reflectivity, `Diffusion` is a config constant outside zones. The 45° sector fills only drive the
occlusion direction shifts (`DirectionOffsets`) and are irrelevant to the reverb.

### `ReverbZone` (AudioSystem; Start 0x548110, OnTriggerEnter 0x548370, OnTriggerExit 0x548640, Initialize 0x548730) and `CalculateReverbOverride`
`ReverbZone` fields: `_reverbTime@0x20, _diffusion@0x24, _roomSize@0x28, _outdoorness@0x2C` (floats;
diffable-cs mistypes them `int`), `_dynamicReverb@0x30 = AudioManager.AudioDynamicReverb`,
`_playerCollider@0x38 = AudioManager.PlayerTransform.GetComponent<Collider>()`, `<InZone>@0x40`.
`OnTriggerEnter(other)`: if `other == _playerCollider`: `InZone = true`, add self to
`_reverbZones` (if absent), `CalculateReverbOverride()`, `_inReverbZone = true`. `OnTriggerExit` /
`OnDisable` → `ExitReverbZone` (remove, recompute). A zone whose collider is not a trigger logs
"`'s ReverbZone will not work becuase the attached collider is not a trigger`".
```
CalculateReverbOverride():            // for each of the four: average over zones whose value > -1, else -1
  _roomSizeOverride    = avg(z._roomSize    where > -1) ?? -1
  _outdoornessOverride = avg(z._outdoorness where > -1) ?? -1
  _reverbTimeOverride  = avg(z._reverbTime  where > -1) ?? -1
  _diffusionOverride   = avg(z._diffusion   where > -1) ?? -1
```
Zone values therefore replace the *targets*; the same lerps still smooth into them. Whether the
starting-area hallway is a `ReverbZone` or plain raycast geometry is scene data (not read); the
mod does not care, because it reads the smoothed result.

### `UpdateReverb()` — the fourteen mixer floats (every LateUpdate, on `Mixer` = main mixer)
```
if (Bypass) { Mixer.SetFloat("DryLevel", 0); Mixer.SetFloat("Room", -10000); return; }
CalculateReverbOverride();
if (_raycastJobHandle.IsCompleted && _raycastJobHandleUpwards.IsCompleted) { Complete both; CalculateReverb(); RaycastsReverb(); }

RS = RoomSize; O = Outdoorness; RT = ReverbTime; D = Diffusion          // all 0..1
DSP_DryLevel     = -150 - 250*RS                                   // mB   (-150 .. -400)
DSP_Room         = -150 - 400*O - 200*RS                            // mB   (-150 .. -750)
DSP_RoomHF       = -1000*D                                          // mB
DSP_RoomLF       = -1000 - 700*RS + 600*O                           // mB
DSP_DecayTime    = clamp(15.9*RT^4 + 0.1, 0.1, 16)                  // s
DSP_DecayHFRatio = 0.8 - 0.5*D
DSP_Reflections  = clamp(1100*(1 - O) - 10600*RS, -10000, 500)      // mB
DSP_ReflectDelay = 0.15*RS                                          // s
DSP_Reverb       = 900*RT - 900 - 2500*O                            // mB
DSP_ReverbDelay  = 0.1*RS                                           // s
DSP_HFReference  = 5000 - 2000*O                                    // Hz
DSP_LFReference  = 300 + 300*O                                      // Hz
DSP_Diffusion    = 100*D                                            // %
DSP_Density      = 50 + 50*RS                                       // %
Mixer.SetFloat("DryLevel", DSP_DryLevel); ... Mixer.SetFloat("Density", DSP_Density);   // 14 calls, MixerParameters names
```
Constants: `-150 0x183977238`, `250 0x183976F4C`, `200 0x183976F38`, `400 0x183976F8C`,
`0.1 0x1839765EC`, `-1000 0x18397725C`, `700 0x183976FC0`, `600 0x183976FB8`, `15.9 0x183976DC0`,
`16 0x183976DC4`, `0.5 0x183976768`, `0.8 0x1839767E8`, `-10000 0x183977278`, `1100 0x183976FE4`,
`-10600 0x18397727C`, `500 0x183976FA8`, `100 0x183976EEC`, `0.15 0x183976640`, `900 0x183976FD4`,
`2500 0x18397700C`, `2000 0x183977000`, `5000 0x183977024`, `300 0x183976F6C`, `50 0x183976E98`.

Worked example, an indoor corridor (`O 0.1, RS 0.3, RT 0.6, D 0.5`): `DryLevel -225, Room -250 mB
(-2.5 dB), RoomHF -500, RoomLF -1150, DecayTime 2.16 s, DecayHFRatio 0.55, Reflections -2190,
ReflectDelay 45 ms, Reverb -610, ReverbDelay 30 ms, HFReference 4800, LFReference 330, Diffusion
50, Density 65`. Outdoors (`O 0.9, RS 0.8, RT 0.3`): `Room -670 mB, Reflections -8370 (the clamp only bites at RS >= 0.94),
Reverb -2880, DecayTime 0.23 s` — effectively no room. That contrast is the hallway effect.

### `UpdateEcho()` / `RaycastEcho()`
Unchanged from the wiring doc §1.1 (6 sectors x 10 rays, buckets at 50 m / 200 m, sea-level
rejection `y < -0.9`, ratios over 500 rays, smoothed at `clamp01(dt*2)` into `<EchoRatioFlat>` +0x130).
They feed `SelfEcho` / `EchoRemote` and the `echo mixer`, not the environment reverb.

---

## 2. The other writers of SFX-Reverb parameter sets

Every `AudioMixer.SetFloat` caller in the game (Assembly-CSharp + AudioSystem): `AudioDynamicReverb.UpdateReverb`,
`AudioBasicReverb.UpdateReverb`, `GlobalAudioEffects.{Update, ResetAll, ResetSpeechlessness, SetBlindfold, SetHeadphone,
SetSFXBusVol, SetMusic3DVol, SetSuperWetEnding, SetSuperWetBlackTower, SetMasterLimiterThreshold}`,
`SelfReverb.Update`, `SelfEcho.LateUpdate`, `PlayerVoicePlaybackControl.{PlayVoice, Update}`, `VoicePlayer.Update`,
`VoiceSimulator.{OnEnable, Update}`, `AmbiencePlayer.LateUpdate`, `StandaloneOcclusion.Update`,
`AlternativeInteriorAmb`, `PeckEffectHeadset.Peck`, `PeckEffectPipeVideoAudio.Update`, `EndingTransition`, `LoadingMenu.OnEnable`.
No snapshot transitions exist anywhere (`TransitionTo*` has no callers); the mixers are driven by
`SetFloat` alone. The three that matter here:

### 2.1 `GlobalAudioEffects.Update()` — RVA 0x431EE0 (main thread, Update phase)
```
SelfVoiceBus.BusVolume.Volume = VoiceAudioSettingsVol * BusVolume.<+0x10>
v = 20*log10(max(VoiceNormalVol * VoiceAudioSettingsVol, 1e-4))            // VoiceNormalVol +0xC4 (ctor 1.0), VoiceAudioSettingsVol +0xC0
Mixer.SetFloat("Voice_Dry", v);  Mixer.SetFloat("Voice_Wet", v)              // main mixer, the two voice buses: SAME level
Mixer.SetFloat("Voice_SuperWet", 20*log10(max(VoiceSuperWetVol * VoiceAudioSettingsVol, 1e-4)))
sp = WorldManager.localPlayerCharacter.speechless._speechlessness
VoiceMixer "VoicePitch" = 1 - sp*SpeechlessPitchDeduction; WalkieTalkieMixer "WalkieTalkiePitch", MegaphoneMixers[i] "MegaphonePitch", Mixer "PropPitch" likewise; "SuperWetPitch" = 1 - sp^0.4*MusicPitchDeduction; "BiomeAmbPitch" = 1 - sp*SpeechlessPitchDeduction*4.5 (0x183976C78)
Mixer "SuperWet_Speechlessness" = (1 - sp^0.4) * -80;   Mixer "MasterWet" = sp^10 * -80          // 0 dB unless speechless
adr = AudioManager.Instance.AudioDynamicReverb
train speakers: "DryLvl" = -10000, "WetLvl" = min(1 - clamp01(dist/300), 1 - adr.ReverbTime, 1 - adr.Outdoorness) * -3000
Mixer "FootstepVol" / "FootstepDry" / "FootstepSuperWet" from adr.Outdoorness / adr.ReverbTime (footstep-only)
StageSpeakerMixer "ReverbHF" / "ReverbLvl" = curves of the listener's distance to StageSpeakerPosition
ImpactsMixer "ImpactsHP" = 10 + 200 * u*(2-u),  u = clamp01(adr.ReverbTime - adr.RoomSize)
_worldSFXFadeIn ramp -> SetSFXBusVol(fade^4), Mixer "Music3DVol"
VoiceMixer.SetFloat("Room",       adr.DSP_Room);   ... "RoomHF" "RoomLF" "DecayTime" "DecayHFRatio" "Reflections" "ReflectDelay"
                                                        "Reverb" "ReverbDelay" "HFReference" "LFReference" "Diffusion" "Density"   // 13, no DryLevel
```
The last block is the **`Reverb Boost` return of the voice mixer** (§0): its SFX Reverb keeps the
asset's `DryLevel -10000` and otherwise tracks the environment reverb one frame late (`Update`
copies what the previous `LateUpdate` computed).

### 2.2 `SelfReverb` (Assembly-CSharp; Start 0x443070, Update 0x443160) — the `WET_*` set
`_mixer@0x20 = AudioManager.AudioDynamicReverb.Mixer` (the main mixer), `_reverb@0x28`. `Update`
first snaps its own transform to the listener (`AudioManager.ListenerController.transform`
position/rotation — decorative), then, with `RS/O/RT` from `_reverb` and
`e = AudioUtil.EaseInOut(RT, 0.75, 3.7)`:
```
WET_DryLevel = -10000;  WET_ReflectDelay = 0;  WET_ReverbDelay = 0;  WET_Diffusion = 100
WET_Room         = -2200 - 300*O + 200*RS + 1500*e
WET_RoomHF       = 3000*max(RS, e) - 6500
WET_RoomLF       = -700 - 300*RS - 100*O
WET_DecayTime    = clamp(19.8*e + 0.2, 0.2, 20)
WET_DecayHFRatio = 0.6*RS*RT + 0.1
WET_Reflections  = clamp(-1500*RS - 500*O, -2000, -200)
WET_Reverb       = -700 - 800*O + 700*e
WET_HFReference  = 5000 - 2000*O;   WET_LFReference = 250 + 350*O;   WET_Density = 50 + 50*RS
```
(constants `0.75 0x1839767D4, 3.7 0x183976C20, -2200 0x183977270, 1500 0x183976FF4, 3000 0x183977010,
6500 0x18397702C, -700 0x183977258, 0.2 0x183976678, 19.8 0x183976DE8, 20 0x183976DEC, 0.6 0x183976784,
-2000 0x18397726C, -1500 0x183977268, -200 0x183977244, 800 0x183976FC8, 350 0x183976F7C`).
These land on `Master Faint Wet`, whose only feeder is the `Self Voice` group (the
`LocalVoicePlayer` / `SelfVoiceBus` monitor, wiring doc §2). Remote voices never pass through it.
`AudioUtil.EaseInOut(x, c, s)` (RVA 0x539F20): `t = (1-c)x / ((1-x)c + (1-c)x)` (or `x` if the
denominator is 0); `t < 0.5 ? t / ((1-2t)s + 2t) : 1 - (1-t) / ((2t-1)s + 2(1-t))`.

### 2.3 `AudioBasicReverb` (AudioSystem; UpdateReverb 0x5026D0) — the `Basic` alternative
Only instantiated when `DynamicReverbConfig.Basic` is true (then `AudioManager.AudioDynamicReverb`
is null and `AudioBasicReverb` +0x140 is set). Fields: `Bypass@0x20, Mixer@0x28, DryLevel..Density
@0x30..0x64 (public getters), _reverbZones@0x68 (List<BasicReverbZone>)`; const `LERP_SPEED 5`.
`UpdateReverb`: sort zones by `Priority`; target = the highest-priority zone's `BasicReverbConfig`
(zones sharing that priority are averaged pairwise); with no zone the target is Unity's off preset
(`DryLevel 0, Room -10000, RoomHF 0, RoomLF 0, DecayTime 1, DecayHFRatio 0.5, Reflections -10000,
ReflectDelay 0.02, Reverb 0, ReverbDelay 0.04, HFReference 5000, LFReference 250, Diffusion 100,
Density 100`); each of the 14 lerps at `clamp01(dt*5)`; then the same 14 `SetFloat`s on `Mixer`
(`Bypass` → `DryLevel 0, Room -10000`). Same effect, same names, different driver.

---

## 3. What a listener hears on a nearby remote voice (distance 0, no fall, no height)

From `PlayerVoicePlaybackControl.Update` at `d = 0`: `Dry{n} = 0 dB, High{n} = 0 dB,
ReverbFallWet{n} = -80 dB` (unless the speaker is falling outdoors), `ReverbBoostWet{n} = -80 dB`.
Following the map in §0, with `v` the Filter-Stage output of the pooled source (after Unity's
spatialisation, unity at 0 m):
```
voice mixer:   Raw/n:  pre-fader sends silent;  post-fader Low = LP250(v) at 0 dB, High = HP250(v) at High{n} = 0 dB
               Dry (-3 dB, Pitch Shifter at 1.0):   bus = 10^(-3/20) * (LP250(v) + HP250(v)) ≈ 10^(-3/20) * v
main mixer:    Voice -> VOICE DRY BUS (-6 dB) -> Master Dry:                       dry  = 10^(-6/20) * bus * Voice_Dry_lin
               Voice -> VOICE WET BUS (0 dB)  -> Master Wet (MasterWet, 0 dB):     wet  = SfxReverb_E(bus * Voice_Wet_lin)
                     SfxReverb_E(x) = 10^(DSP_DryLevel/2000) * x + reverb(x; DSP_Room .. DSP_Density)
               Voice -> VOICE SUPER WET BUS (0 dB) -> Speechlessness/Blindfold/Ending/BlackTower at -80 dB: silent in normal play
               Master Group: MasterLP 22 kHz, ParamEQ 3.3 kHz x1.0, ParamEQ 1 kHz x1.0 (unity unless headphone/blindfold)
               Master: Duck Volume limiter, threshold MasterLimiterThreshold (-3 dB), ratio 10, attack 0, release 0.125 s, knee 20
out = dry + wet          (Voice_Dry == Voice_Wet, so the voice slider is a common factor)
```
So relative to `bus` the listener gets **two coherent dry copies**, `10^(-6/20) = 0.50` plus the
reverb's own `10^(DSP_DryLevel/2000) = 0.84 .. 0.63`, i.e. `+2.6 .. +1.1 dB` net, and a reverb whose
wet level is `DSP_Room` (`-1.5 .. -7.5 dB` below the signal entering the reverb) shaped by the other
twelve parameters. Everything else on the spatial SFX buses (footsteps, props, foley, ambience)
receives the same reverb at its own send level, which is why the hallway "sounds like a room" as a
whole. The megaphone and walkie-talkie voices join at `Megaphone` (-6 / 0 dB) and `Walkie Talkie`
(-3 / -9 dB); the cliff echo (`Echo` group) goes dry-only.

---

## 4. Cross-check: what the per-voice sends feed into

| send (voice mixer `Raw/n`) | position | target | return level | SFX Reverb on the return |
|---|---|---|---|---|
|`ReverbFallWet{n}` (`20*log10(_fallWetLvl)`, DSP doc §4)|pre-fader|`Reverb Fall`|+3 dB|**fixed** (no exposed params): `DryLevel -10000, Room 0, RoomHF -3000, DecayTime 4.0 s, DecayHFRatio 2.0, Reflections -2000, ReflectDelay 0.3 s, Reverb -1500, ReverbDelay 0.1 s, Diffusion 100, Density 100, HFReference 5000, RoomLF -4000, LFReference 250`|
|`ReverbBoostWet{n}` (`20*log10(boost)`)|pre-fader|`Reverb Boost`|0 dB|`DryLevel -10000` fixed; `Room .. Density` = the environment reverb's thirteen values, one frame late (§2.1)|
|`High{n}` (`occl * -30`)|post-fader|`High` (Highpass Simple 250 Hz)|0 dB|none; summed with the fixed 0 dB `Low` send (Lowpass Simple 250 Hz) under `Dry`|

Consequences for the existing `TravelEar.Core.MixerStageModel` / `MixerStage` (DESIGN.md, ADR-0002):
- `out = x*dry + reverb(x*(fallWet + boostWet))` is the right shape (both sends are pre-fader, so
  `Dry{n}` does not scale them), but the two sends deserve two reverbs: the fall reverb is a fixed
  4 s, dark (`RoomHF -3000`, `DecayHFRatio 2.0` means highs ring *longer* than mids), late-heavy
  (`Reflections -2000`, `Reverb -1500`, 0.3 s + 0.1 s onsets) tail at +3 dB; the boost reverb is a
  fully wet copy of the environment reverb (so the environment stage below can be reused for it:
  same parameters, `DryLevel` forced to `-10000`).
- The reverb returns sit under the voice mixer's `Master`, so their output then goes through the
  main-mixer dry/wet buses like the direct voice: the environment reverb is applied to the fall
  and boost tails too.
- `High{n}` is a 250 Hz band split, not a 3 kHz shelf: `direct = LP250(x) + 10^(High/20) * HP250(x)`
  with Unity's one-parameter "Simple" filters *(order inferred: first-order)*. At the Self-Ear
  `High = 0` and the split sums back to `x`, so nothing changes for v1; note it for a later
  occlusion-at-distance feature.
- The voice mixer adds a constant `-3 dB` (`Dry` group) and a Pitch Shifter at pitch 1.0
  (`VoicePitch`, only moved by speechlessness). The main mixer adds `-6 dB` on the dry bus.

---

## 5. What this means for the mod

### Inputs to read (all public; main thread, once per frame, after `AudioManager.LateUpdate` or accept a one-frame lag)
```
var am  = AudioManager.Instance;                     // null before the audio system is up
var adr = am.AudioDynamicReverb;                     // property, backing +0x138; null in Basic mode
var abr = am.AudioBasicReverb;                       // property, backing +0x140; null in Dynamic mode
bool bypass = adr != null ? adr.Bypass /*+0x20*/ : abr.Bypass /*+0x20*/;
float dryLevel = adr.DSP_DryLevel, room = adr.DSP_Room, roomHF = adr.DSP_RoomHF, roomLF = adr.DSP_RoomLF,
      decayTime = adr.DSP_DecayTime, decayHFRatio = adr.DSP_DecayHFRatio, reflections = adr.DSP_Reflections,
      reflectDelay = adr.DSP_ReflectDelay, reverb = adr.DSP_Reverb, reverbDelay = adr.DSP_ReverbDelay,
      hfReference = adr.DSP_HFReference, lfReference = adr.DSP_LFReference, diffusion = adr.DSP_Diffusion, density = adr.DSP_Density;
      // Basic mode: abr.DryLevel .. abr.Density (offsets 0x30..0x64), same meaning
      // cross-check / alternative: GlobalAudioEffects.Instance.Mixer.GetFloat("DryLevel", out f) etc. (exposed on the main mixer; the
      // voice mixer exposes the 13-name copy on the Reverb Boost return)
var gae = GlobalAudioEffects.Instance;
float masterWetDb = 0f; gae.Mixer.GetFloat("MasterWet", out masterWetDb);           // 0 unless speechless (sp^10 * -80)
float voiceBusDb = 20*log10(max(gae.VoiceNormalVol * gae.VoiceAudioSettingsVol, 1e-4));   // = Voice_Dry = Voice_Wet (optional common factor)
```
`RoomSize / Outdoorness / ReverbTime / Diffusion` (+0x30..0x3C) are only needed if the mod wants to
re-derive the fourteen (formulas in §1) for a unit test against the live `DSP_*`; the game already
smooths them (`RoomSize` at `clamp01(5 dt)`, the other three at `clamp01(dt)`), so the mod should
**not** add smoothing of its own. `Bypass` true → `DryLevel 0, Room -10000` (no reverb). Symbols to
add to `GameSymbols.Bind` so a rename hard-fails: `AudioManager.AudioDynamicReverb`,
`AudioManager.AudioBasicReverb`, `AudioDynamicReverb.Bypass`, the fourteen `DSP_*` getters (or
`AudioBasicReverb`'s fourteen), `GlobalAudioEffects.Mixer`, the mixer names `"MasterWet"` and the
fourteen `MixerParameters` constants (`DryLevel .. Density`).

### Core model (`EnvironmentReverbModel`, main thread → parameter snapshot per frame)
No formulas to port for the parameters: the snapshot *is* the fourteen `DSP_*` values plus
`masterWetDb` (and `bypass`). The fixed gains of the chain (§3) are constants of the model:
```
busGain   = 10^(-3/20)                                  // voice mixer "Dry" group
dryBus    = 10^(-6/20)                                  // Voice -> VOICE DRY BUS send
wetBus    = 1                                           // Voice -> VOICE WET BUS send (0 dB)
returnGain= 10^(masterWetDb/20)                         // Master Wet volume (pre-reverb; 1 in normal play)
```
Order in the encoder-thread chain: **last**, on the sum of the Clean voice (after `MixerStage`) and
the Megaphone voice (`MegaphoneVoice`, which the game also routes into the same two buses at
`-6 / 0 dB`), because both enter the environment reverb together on a listener's machine.

### DSP (`TravelEar.Core.SfxReverb`, encoder thread, mono 48 kHz)
```
x   = busGain * voice
dry = dryBus * x
wet = returnGain * ( 10^(DryLevel/2000) * x + Reverb(x) )
out = dry + wet
```
Unity's "SFX Reverb" is FMOD's I3DL2 reverb; map its parameters onto the existing Freeverb-style
`TravelEar.Core.Reverb` (eight combs, four allpasses) as follows:

| SFX Reverb parameter | maps to | fidelity |
|---|---|---|
|`DryLevel` (mB)|`10^(DryLevel/2000)` gain on the un-reverbed copy inside the wet path|exact|
|`Room` (mB)|overall wet gain `g_room = 10^(Room/2000)`|exact as a level; spectral balance of the wet is approximate|
|`Reflections` (mB), `ReflectDelay` (s)|early reflections: `g_early = g_room * 10^(Reflections/2000)`, a short multi-tap FIR starting `ReflectDelay` after the dry (I3DL2: Reflections is relative to Room)|level and onset exact; tap pattern approximate|
|`Reverb` (mB), `ReverbDelay` (s)|late tail: `g_late = g_room * 10^(Reverb/2000)`, comb bank input delayed `ReflectDelay + ReverbDelay`|level and onset exact; network approximate|
|`DecayTime` (s)|comb feedback `g_i = 10^(-3 L_i / (DecayTime * fs))` (RT60 at mid frequencies)|exact RT60 target|
|`DecayHFRatio`|in-loop one-pole damping so that the loop gain at `HFReference` gives RT60 `DecayTime * DecayHFRatio` (`g_hf,i = 10^(-3 L_i / (DecayTime * DecayHFRatio * fs))`, damping `d_i = clamp(1 - g_hf,i / g_i, 0, 0.99)`); ratio > 1 (the fall reverb) needs a high-boost in the loop instead — cap at 1 and note it|approximate|
|`RoomHF` (mB) at `HFReference` (Hz)|RBJ high shelf on the wet output, gain `RoomHF/100` dB, corner `HFReference` (DSP doc §5 table)|approximate (I3DL2 defines it as the room level at the HF reference)|
|`RoomLF` (mB) at `LFReference` (Hz)|RBJ low shelf on the wet output, gain `RoomLF/100` dB, corner `LFReference`|approximate|
|`Diffusion` (%)|allpass coefficient `k = 0.3 + 0.4 * Diffusion/100`|approximate|
|`Density` (%)|spread of the comb lengths (`L_i = L0_i * (0.6 + 0.4 * Density/100)`) — modal density|approximate|
|HP / LP|none in this effect (the megaphone's `HPFrequency` / `LPFrequency` are separate effects on its own mixer)|—|

Exact: all gains, the two onset delays, the mid-band RT60, the shelf corner frequencies and gains
as targets. Approximate: FMOD's actual comb/allpass topology and its reading of Diffusion/Density,
the early-reflection pattern, the "Simple" 250 Hz split (irrelevant at `High = 0`), the Pitch
Shifter at 1.0 (phase-vocoder smear, ignored), the master Duck limiter (`-3 dB` threshold, ratio
10, 0 / 0.125 s; optional port with `TravelEar.Core.Compressor`), and stereo: the Sink voice is
mono, so the decorrelated stereo tail of the real effect is lost. Ranges to clamp inputs to (Unity's
own): `DryLevel/Room/RoomHF/RoomLF -10000..0 mB, DecayTime 0.1..20 s, DecayHFRatio 0.1..2,
Reflections -10000..1000, ReflectDelay 0..0.3 s, Reverb -10000..2000, ReverbDelay 0..0.1 s,
HFReference 20..20000, LFReference 20..1000, Diffusion/Density 0..100`.

### Reuse for the per-voice sends (§4)
The same `SfxReverb` with `DryLevel = -10000` and the fourteen values above is the `Reverb Boost`
return (`ReverbBoostWet{n}`, +0 dB); a second instance with the fixed fall parameters (§4, +3 dB
return) is the `Reverb Fall` return. Both outputs join `x` *before* the environment stage. That
replaces the single config-decay Freeverb of M3 T1 with game-derived parameters; keep
`Fidelity.ReverbDecaySeconds` only as an override knob.

### Config / toggles
`Fidelity.EnvironmentReverb` (master, default on), `EnvironmentReverbDryCopy` (the
`10^(DryLevel/2000)` second dry copy, default on — it is what makes a nearby voice sit "in" the room
rather than beside it), `EnvironmentReverbBusGains` (apply the `-3 / -6 dB` constants, default on),
`EnvironmentReverbVoiceSlider` (multiply by `Voice_Dry/Voice_Wet`, default off: the Sink level
convention already couples to the slider through `TargetARV`). Unreadable inputs (no
`AudioManager`, neither reverb component, a failed `GetFloat("MasterWet")`) bypass the stage and log
once (`REQ-HAZARD-NO-PARTIAL-FIDELITY`).

### Calibration
Record the same speech on a second client standing in the starting hallway and outdoors; compare
tail length (should match `DSP_DecayTime` within the HF-ratio caveat), the dry/wet ratio (set by
`DSP_Room` and the two dry copies, no free parameter), and the wet's low/high balance (the two
shelves). Log `adr.RoomSize / Outdoorness / ReverbTime / Diffusion` and the fourteen `DSP_*` every
10 s alongside the Offset line so a recording can be replayed against known parameters.

---

## 6. Unreadable / inferred

**`AudioMixer.GetFloat(string, out float)` cannot be called in this game** (M3 run 3,
2026-09-09). The method is stripped from the IL2CPP build and Il2CppInterop unstrips it with
Unity 6's managed body, which pins the name as an `Il2CppSystem.ReadOnlySpan<char>` whose
`GetPinnableReference` is stripped as well: every call throws
`MissingMethodException: '!0 ByRef Il2CppSystem.ReadOnlySpan`1.GetPinnableReference()'`. The
native `UnityEngine.Audio.AudioMixer::GetFloat_Injected` icall is not registered in
GameAssembly.dll either (only `SetFloat_Injected` is), so no read path exists. `MasterWet` is
read instead from the game's own writes: a Harmony postfix on `AudioMixer.SetFloat(string,
float)` (`MixerFloats`), which the game does call (`MasterWet` and `MasterWetMatch` are string
literals in global-metadata.dat). Until the first write the mod assumes the mixer asset's nominal
0 dB and says so once; the asset's actual default has not been read from the mixer YAML yet.
- Built-in mixer effect type ids (§0) are inferred from parameter counts/order and the exposed
  names that land on them; the SFX Reverb identification (`type 17`, 14 parameters in Unity's
  documented order, carrying `DryLevel .. Density`) is unambiguous, the rest are labels only.
- Whether the hallway is a `ReverbZone` or raycast geometry, the config curves' shapes,
  `InitialDiffusion`, `CollideInfoSize`, `ActualRange`, ray counts: asset/scene data, readable at
  runtime through `AudioManager.AudioConfig.DynamicReverbConfig` if ever needed; the mod does not
  need them because it reads the smoothed outputs.
- `ReverbLookup` holding `AudioMaterial.ReverbAbsorption` (so reflectivity = `1 - absorption`) is
  inferred from the field names; the code only shows `1 - lookup[mat]`.
- The "Simple" lowpass/highpass filter order (250 Hz split) and the exact FMOD SFX Reverb network
  are not derivable from the game; the mapping in §5 is the proposal to calibrate against.
- `VoiceNormalVol` is written only by `GlobalAudioEffects` itself (ctor / reset to 1.0);
  `VoiceAudioSettingsVol` comes from the settings slider (setter not seen in the scanned
  assemblies); both are readable.

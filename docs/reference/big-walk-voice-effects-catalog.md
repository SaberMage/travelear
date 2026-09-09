# Big Walk — every effect the game can apply to a remote player's voice on a listener's machine

> **Scope.** One remote player speaks; you are the listener. This note catalogues *everything* the
> game does to that voice between the Opus decoder and your speakers, in every game state, with
> the trigger, the mechanism, the formula and the constants. It is the conformance checklist the
> mod's Local Voice chain is measured against: TravelEar's job is to render the local player's
> voice the way a peer's machine would render it, so every row here is either "the mod reproduces
> this" or "the mod is missing this".
>
> Companions: [`big-walk-voice-dsp.md`](big-walk-voice-dsp.md) (the per-voice in-process chain),
> [`big-walk-local-voice-wiring.md`](big-walk-local-voice-wiring.md) (SelfEcho, `VoicePlayer`,
> megaphone), [`big-walk-environment-reverb.md`](big-walk-environment-reverb.md)
> (`AudioDynamicReverb` and the mixer buses). Section 9 lists the errata this note found in those
> three; where they disagree with this note, **this note wins** — every claim here was re-derived
> from the binary or the mixer assets for this pass.

## Sources and how to re-check anything here

| source | what it gives | how it was read |
|---|---|---|
| `Big Walk_Data\data.unity3d` | the 38 `AudioMixerController` assets: group tree, effect chains and their fixed parameters, send targets, exposed-parameter names (CRC32) and **their asset defaults**, snapshot value tables | UnityPy 1.25.3, `obj.read_typetree()`; exposed names recovered by brute-forcing `zlib.crc32(name)` against identifiers pulled from `global-metadata.dat` |
| `GameAssembly.dll` (image base `0x180000000`) | every float constant, by VA, via a PE section walk | `struct.unpack_from('<f', …)` at the file offset of the VA |
| Cpp2IL 2022.1.0-pre.21 `--output-as diffable-cs` | field offsets, RVAs, member lists | pre-dumped tree |
| Cpp2IL `--output-as isil` | method bodies (x64 + ISIL) | pre-dumped tree |
| Cpp2IL `--output-as dll_il_recovery --use-processor attributeinjector,callanalyzer` | the call graph (`[Calls]` / `[CalledBy]` / `[CallerCount]` attributes) | `ilspycmd -p -o <dir>` over the recovered `Assembly-CSharp.dll` / `AudioSystem.dll`, then grep |
| `global-metadata.dat` | string literals (mixer parameter names, tokens, room names) | `grep -a -o` |

Every numeric constant below carries either its VA in `GameAssembly.dll` or the mixer asset it
came from. Values marked *(prefab/scene data)* are serialized on assets that were not read; they
are readable at runtime through the public fields named beside them.

## Conventions

- **Listener-side** — driven by the state of the machine doing the listening (its room, its
  blindfold, its position). One instance of the effect serves every voice.
- **Speaker-side** — driven by the state of the player who is talking, but still evaluated *on the
  listener's machine* from replicated state. Big Walk has no server-side voice DSP: every
  "speaker-side" term below is a local read of the remote `PlayerCharacter` / `PlayerNetworking`.
- **Geometric** — driven by the relation between the two (distance, angle, occlusion, height).
- dB unless stated. Unity mixer levels are dB; SFX Reverb parameters are **millibels (mB)**,
  seconds, Hz or percent, in Unity's documented order.
- `n` is the voice channel, 1..12 (`PlayerVoicePlaybackControl._index + 1`).
- `sp` is `PlayerSpeechless._speechlessness` (0..1) — see section 5.5.
- `adr` is `AudioManager.Instance.AudioDynamicReverb`.

---

## 1. Scenario table

Severity is for the mod: **high** = the listener plainly hears it and Local Voice does not carry
it; **medium** = audible in the situation it applies to; **low** = inaudible or unreachable in
normal play.

| # | Scenario | Trigger (exact state) | Mechanism | Where | Side | Mod today | Gap / severity |
|---|---|---|---|---|---|---|---|
| 1 | **Decode + AGC + compressor + soft clip** | always | `SamplePlaybackComponent.ProcessSamples` (RVA `0x6F2DA0`) → `VoiceCompressor` + `SoftClip` + `VoiceMakeupGain` | DSP doc 1-3 | speaker signal | `VoiceDynamics`, `VoiceCompressor`, `VoiceMakeupGain` port | covered |
| 2 | **400 Hz "through a wall" EQ** | always present; wet mix from distance + angle | `BiquadFilters` PeakingEQ 400 Hz Q 0.3 +30 dB, `Vol 0.03`, `DryWet = clamp01(t·(1−(1−d)(1−a)))` | 5.1 | geometric | `PeakingEq`, wet from `Fidelity.SelfEarEqDryWet` | covered (wet ≈ 0 at the Self-Ear) |
| 3 | **Distance / spatial attenuation** (`Dry{n}`) | always | `Dry{n} = max(−80, 20·log10(max(att, boost/3·h, 1e−4)))` | 5.1 | geometric | `MixerStageModel` | covered (0 dB at the Self-Ear) |
| 4 | **Occlusion band split** (`High{n}`) | `GetX(OcclusionLevel) > 0` | `High{n} = occl·−30 dB` on a **250 Hz high band**, summed with a fixed 0 dB 250 Hz low band | 5.1 | geometric | `MixerStage` models it as a 3 kHz **high shelf** | wrong filter shape; inaudible at the Self-Ear — **low** |
| 4b | **Voice blocking materials** | a ray between listener and speaker hits a material in `AudioMaterialConfig.VoiceBlockingMaterials` | `AudioOcclusionBase.VoiceBlockingLvl` (a membership average, lerped) exposed as `GetX(VoiceBlock)`; consumed **only** by whatever RTPC curves the voice Cue defines — data, not code. Forced to 0 when the *local* player is `isGhost && !isAudioGhost` | 5.1 | geometric | nothing | **undetermined** — the effect lives in the Cue asset's RTPC curves, which were not read |
| 5 | **Fall reverb** (`ReverbFallWet{n}`) | speaker's `PlayerFaller.isInDanger` | pre-fader send into the voice mixer's `Reverb Fall` return (+3 dB) with a **fixed** 4 s SFX Reverb | 5.2 | speaker | `MixerStage` (`Fidelity.MixerReverbFall`) with a generic Freeverb | reverb character approximate — **medium** |
| 6 | **Height / indoor reverb boost** (`ReverbBoostWet{n}`) | speaker above the listener, indoors, within 45 m | pre-fader send into `Reverb Boost` (0 dB), a fully-wet copy of the environment reverb | 5.3 | geometric + listener | `MixerStage` (`Fidelity.MixerReverbBoost`); **wrong input** — uses a "global voice volume" where the game uses `adr.ReverbTime` | silent at the Self-Ear (`att = 1`) — **low**, but the ported model is wrong |
| 7 | **Environment reverb** | always | main mixer `Master Wet` SFX Reverb, 14 floats rewritten every `LateUpdate` from `RoomSize/Outdoorness/ReverbTime/Diffusion` | env-reverb doc 1 | listener | `EnvironmentReverb` + `SfxReverb`, live floats | covered (approximate network) |
| 7b | **Indoor voice attenuation** (`_outdoornessVol`) | always | an `AudioVolume` in the source's volume chain: `Volume += ((adr.Outdoorness·0.5 + 0.5) − Volume)·clamp01(dt·3)` — **0.5 linear (−6 dB) indoors, 1.0 outdoors** | 5.1b | **listener** | **nothing** | **high** — a flat −6 dB on every voice indoors, exactly where the mod is "a little hot" |
| 8 | **Red bells — voice volume** | **speaker** inside a `SpeechlessZone`'s `outerRadius` | `PlayerVoicePlaybackControl._speechlessVol.Volume = lerp(v, 1 − sp_speaker, dt·5)`, an `AudioVolume` in the source's volume chain | 5.5 | **speaker** | **nothing** | **high** |
| 9 | **Red bells — voice pitch** | **listener** inside a `SpeechlessZone` | voice mixer `Dry` group Pitch Shifter, `VoicePitch = 1 − sp_listener · SpeechlessPitchDeduction` | 5.5 | **listener** | **nothing** | **high** |
| 10 | **Red bells — super-wet bloom** | listener `sp > 0` | `SuperWet_Speechlessness = (1 − sp^0.4)·−80` opens the `Speechlessness` return; every voice already feeds it at 0 dB through `VOICE SUPER WET BUS`; the return is a fixed 6.8 s SFX Reverb + Chorus at group pitch `SuperWetPitch = 1 − sp^0.4·MusicPitchDeduction` | 5.5 | **listener** | **nothing** | **high** |
| 11 | **Red bells — environment-reverb kill** | listener `sp` near 1 | `MasterWet = sp^10 · −80` | 5.5 | listener | `EnvironmentReverb` already reads `MasterWet` from `MixerFloats` | **covered** — the one speechlessness term the mod tracks |
| 12 | **Megaphone voice** | a `RadioVoiceAssigner` whose `voicePlayer.PlayerType == Megaphone` is broadcasting | a second `VoicePlayer` on the prop, fed from the same ring: `BitCrusher` + 300 Hz HP in process, then the whole `megaphone mixer N` chain, then `megaphone master` Dry/Wet | 5.9 | speaker + geometric | `MegaphoneVoice` (crusher, HP, two compressors) | missing the mixer's fixed LP 5 kHz, 2.5 kHz ParamEQ, 100 ms Echo and its live 2 s SFX reverb; compressor timings wrong — **medium** |
| 13 | **Walkie-talkie / radio voice** | a `RadioVoiceAssigner` whose `PlayerType == WalkieTalkie` (or `Radio`) is receiving | `BitCrusher` in process, then `radio mixer` / `walkietalkie mixer N` (two ParamEQs, LP 9 kHz, HP 400 Hz, compressor) → `walkietalkie mixer` pitch shifter | 5.10 | speaker | **nothing** (explicitly out of scope) | **medium**, out of scope |
| 14 | **Underwater walkie muffle** | the **receiving** walkie-talkie prop below the water surface (nothing about the speaker's depth is networked) | `WalkieTalkieLP = t2·−21820 + 22000` Hz (22000 → **180 Hz**), `t2` from `WaterDepthSampleData.GetDepth` on the prop's own transform | 5.14 | receiver's prop position | nothing | low (walkie only) |
| 15 | **Cliff echo on a remote voice** | outdoors, both parties' `outdoorness` high | `EchoRemote` adds two more `VoicePlayer`s on the same ring into the `echo mixer`: a 1 s slapback at the speaker, plus a "close" copy 1 m in front of *the listener* fed into the listener's three directional echo emitters | 5.11 | listener × speaker | **nothing** (deferred to v1.1) | **high** outdoors |
| 16 | **Blindfold LPF — the wearer's voice** | the **speaker** pecks a blindfold: `PeckEffectMask.Peck` (RVA `0x4DE9B0`) on every *other* machine calls `pc.lips.playerVoicePlaybackControl.SetBlindFoldMode(true)` | a second `BiquadFilters` is `AddComponent`ed onto the voice source: `LowPass 1500 Hz, Q 0.6`, `_vol 1`, `_dryWet 1` — **fully wet**, no `Bypass` involved | 5.6 | **speaker** | **nothing** | **high while worn** — a blindfolded local player is heard by everyone through a full-wet 1.5 kHz LPF |
| 17 | **Blindfold global tone** | the **listener** pecks a blindfold: `PeckEffectMask.Peck` → `GlobalAudioEffects.SetBlindfold(true)` on that player's machine only | `MasterLP 22000→5000`, `SuperWet_Blindfold −80→−21`, `OceanAmbVol`/`AmbExtVol` `0→−15`, blindfold loop cue | 5.6 | listener | nothing | **medium** (only while the listener is blindfolded) |
| 18 | **Headphone tone** | the **listener** pecks a headset: `PeckEffectHeadset.Peck` (RVA `0x4DDA80`), `isLocalPlayer` only | `MasterLP 22000→16000`, `MasterFreqGain3k 1→0.5`, `MasterFreqGain1k 1→1.5`; also moves the audio listener and adds a Dissonance token | 5.7 | listener | nothing | **medium** |
| 19 | **Ending sequence** | `EndingTransition.SetActive` (from `PeckEffectEndingTransition.OnPeck` RVA `0x4DD9A0`, or `AutomaticDisconnector.StartEndingTransition` RVA `0x3D7960`) | `AudioTransitionUpdate(t)` (RVA `0x4B1EC0`) ramps `SuperWet_Ending`, `VoiceNormalVol`, `VoiceSuperWetVol`, `GAE.VoiceSptialBlend` and `SetSFXBusVol` from serialized `AnimationCurve`s over `t = (Time.time − start)/duration` | 5.8 | listener | nothing | low (scripted, once per run) |
| 20 | **Black tower** | `AlternativeInteriorAmb.Update` (RVA `0x331CC0`) with its `SuperWet` flag set | `SetSuperWetBlackTower(v·0.35)`, `v = ASC._attenuationVol · ASC._rtpcVol` of the tower's ambience emitter | 5.8 | listener | nothing | low |
| 20b | **`VoiceSimulator`** | a `VoiceSimulator` component enabled in a scene | writes `Dry{n}`, `High{n}`, `ReverbFallWet{n}`, `ReverbBoostWet{n}` on a **real voice slot** (`_index` parsed from its GameObject name), so it can fight a live speaker on that channel | 5.13 | n/a | nothing | low (dev tool) |
| 21 | **Ghost voice** | `PlayerNetworking.isGhost` → `PlayerLips.SetGhost(true)` | `TwoDMode = true`: `ScriptableVolume = 1`, `ScriptableSpatialBlend = 0`, `_attenuation = 1`, `_fallWetLvl = 0`, `occl = 0` → `Dry{n} = 0`, `High{n} = 0`, both sends −80 | 5.12 | speaker | n/a — but this **is** the Self-Ear the mod models | validates the mod's model; nothing to add |
| 22 | **2D voice** | `PlayerNetworking.is2DVoice` SyncVar → `PlayerLips.Set2DVoice` | same `TwoDMode` path | 5.12 | speaker | as above | trigger unresolved (section 10) |
| 23 | **Mute / silence** | `WorldManager.SetMuted` (transmit side); `PlayerLips.isSilenced` (Dissonance local mute) | no DSP: the voice is not transmitted, or not decoded | 5.13 | both | `TransmitGate` covers the transmit side | covered |
| 23b | **Voice-channel exhaustion** | more than 12 `PlayerVoicePlaybackControl`s active at once | `TryTakeCue` pops from a 12-entry static `cueStack`; on an empty stack it logs `"can't get a voice cue from the stack for {0}! {1} voice controls are active but only {2} cues exist, so this player stays silent until one frees up."` once and returns false — `Update` then returns early, so the voice is **silent** | 5.13 | listener | n/a for the Self-Ear | low |
| 24 | **Gibberish mode** | `PlayerVoicePlaybackControl.GibberishMode` static | `PlayGibberish` instead of `PlayVoice`: `AmplitudeOnlyMode = true`, an extra `_amplitudeVol`, a `GibberishCue` | 5.13 | listener global | nothing | low (debug/dev) |
| 25 | **Master limiter** | always | main `Master` group: self-send + Duck Volume, threshold `MasterLimiterThreshold` (−3 dB), ratio 10, attack 0, release 0.125 s, knee 20, sidechain 1 | 5.15 | listener | not ported | **medium** (bites on loud speech) |
| 26 | **Master EQ / LP** | always | `Master Group`: Lowpass `MasterLP` (22 kHz), ParamEQ 3300 Hz oct 0.7 gain `MasterFreqGain3k` (1), ParamEQ 1000 Hz oct 0.7 gain `MasterFreqGain1k` (1) | 5.7 | listener | not ported | low at defaults, **medium** with headphone / blindfold |
| 27 | **Voice volume slider** | settings | `Voice_Dry = Voice_Wet = 20·log10(max(VoiceNormalVol·VoiceAudioSettingsVol, 1e−4))`; `Voice_SuperWet` likewise from `VoiceSuperWetVol` | 5.16 | listener | `Fidelity.EnvironmentReverbVoiceSlider` (off by default) | deliberate |
| 28 | **World SFX fade-in** | first seconds of a world | `_worldSFXFadeIn^4` → `SetSFXBusVol`, `Music3DVol` | 5.16 | listener | nothing | low (SFX buses only, no voice bus) |

---

## 2. The per-voice signal path on a listener's machine

```
                     ── one remote speaker, one listener ──

network ─► Dissonance jitter buffer ─► Opus decode ─► SpeechSession.Read
                                                           │
┌──────────────── Unity audio thread, per DSP block ────────┴─────────────────────────┐
│ The pooled AudioSource plays a constant-1.0 clip, so data[] already carries Unity's  │
│ spatial gain envelope: distance rolloff, spread, and the AudioSourceController       │
│ volume chain (which includes _outdoornessVol and _speechlessVol).                    │
│                                                                                     │
│ AudioFilterMixer.OnAudioFilterRead(data, channels):                                 │
│   [0] SamplePlaybackComponent.ProcessSamples                                        │
│         y = SoftClip( VoiceCompressor( decoded * rampTo(MakeupGain) ) )              │
│         data[i] *= y                       <- multiplies into the 1.0 envelope       │
│   [1] BiquadFilters _eqFilter    PeakingEQ 400 Hz Q 0.3 +30 dB Vol 0.03,             │
│                                  DryWet = clamp01(t*(1-(1-d)*(1-a)))                 │
│   [2] BiquadFilters _blindFoldFilter   LowPass 1500 Hz Q 0.6   [bypassed normally]   │
│   data[i] = clamp(data[i], -1, +1)                                                   │
└──────────────────────────────────────────────────────────────────────────────────────┘
                                  │  AudioSource -> mixer group "Voice{n}"
                                  v
========== voice mixer (asset 4531) ===================================================
  Raw (-80 dB, output muted; reaches Master only through its sends)
   +- n   vol = <Dry{n}>                                    (asset default -80)
        |- PRE-fader Send -> Reverb Boost  <ReverbBoostWet{n}>   (default -80)
        |- PRE-fader Send -> Reverb Fall   <ReverbFallWet{n}>    (default -80)
        |- Attenuation
        |- POST Send -> Low   0 dB fixed
        +- POST Send -> High  <High{n}>                          (default 0)
  Low  (0 dB)   Lowpass Simple 250 Hz  --+
  High (0 dB)   Highpass Simple 250 Hz --+
  Dry  (-3 dB)  Pitch Shifter <VoicePitch>=1, FFT 1024, overlap 4   <---+
  Reverb Fall  (+3 dB)  SFX Reverb, FIXED 4 s dark tail          (section 5.2)
  Reverb Boost ( 0 dB)  SFX Reverb, <Room..Density> mirrored from the environment reverb
  Master (0 dB)
=======================================================================================
                                  │  output group = main mixer "Voice"
                                  v
========== main mixer (asset 156) =====================================================
  Voice (0 dB)   Send -> VOICE DRY BUS        -6 dB
                 Send -> VOICE WET BUS         0 dB
                 Send -> VOICE SUPER WET BUS   0 dB      <- always at full level

  VOICE DRY BUS        vol <Voice_Dry>        -> Master Dry   0 dB
  VOICE WET BUS        vol <Voice_Wet>        -> Master Wet   0 dB
  VOICE SUPER WET BUS  vol <Voice_SuperWet>   -> Speechlessness / Blindfold /
                                                 Ending / BlackTower, 0 dB each

  Master Group (0 dB)  Lowpass Simple <MasterLP>=22000
                       ParamEQ 3300 Hz oct 0.7 gain <MasterFreqGain3k>=1
                       ParamEQ 1000 Hz oct 0.7 gain <MasterFreqGain1k>=1
   |- Master Dry        (0 dB)
   |- Master Wet        vol <MasterWet>=0  SFX Reverb <DryLevel..Density>
   |                                        <- THE ENVIRONMENT REVERB
   |- Master Faint Wet  (0 dB)  SFX Reverb <WET_*>   <- self-voice monitor only
   +- Master Super Wet  pitch <SuperWetPitch>=1
        SFX Reverb FIXED { DryLevel -10000, Room 0, RoomHF -2000, DecayTime 6.8 s,
                           DecayHFRatio 0.15, Reflections -10000, ReflectDelay 0.01,
                           Reverb 0, ReverbDelay 0.1, Diffusion 100, Density 25,
                           HFReference 5000, RoomLF 0, LFReference 250 }
        Chorus     FIXED { DryMix 1, Wet1 0.75, Wet2 0.5, Wet3 0.25,
                           Delay 100 ms, Rate 0.5 Hz, Depth 0.5 }
        |- Blindfold       vol <SuperWet_Blindfold>       = -80
        |- Speechlessness  vol <SuperWet_Speechlessness>  = -80
        |- Ending          vol <SuperWet_Ending>          = -80
        +- BlackTower      vol <SuperWet_BlackTower>      = -80

  Master (0 dB)  Attenuation · Send -> own Duck 0 dB ·
                 Duck Volume { Threshold <MasterLimiterThreshold>=-3, Ratio 10,
                               Attack 0, Release 0.125 s, MakeUp 0, Knee 20,
                               Sidechain 1 } · Dissonance Echo Cancellation (plugin)
=======================================================================================
```

Three more routes carry the *same speaker* and sum with the above:

```
Megaphone : the prop's Megaphone VoicePlayer -> megaphone mixer 1..4 -> megaphone master
            (Dry / Wet by <Megaphone{i}Dry> / <Megaphone{i}Wet>)
            -> main "Megaphone" -> VOICE DRY -6 dB / VOICE WET 0 dB
Walkie    : radio mixer | walkietalkie mixer 1..10 -> walkietalkie mixer (pitch shifter)
            -> main "Walkie Talkie" -> VOICE DRY -3 dB / VOICE WET -9 dB
Cliff echo: EchoRemote's two extra VoicePlayers -> echo mixer -> main "Echo"
            -> VOICE DRY 0 dB   (dry only: no environment reverb on the echo)
```

**Effective gain of the normal path at the Self-Ear** (distance 0, so `Dry{n} = 0 dB`,
`High{n} = 0 dB`, both sends at −80 dB):

```
src = (Outdoorness*0.5 + 0.5) * (1 - sp_speaker) * v    # AudioSourceController volume chain
                                                        # (section 5.1b / 5.5), 0.5..1.0 and 0..1
bus = 10^(-3/20) * src                                  # voice mixer "Dry" group
dry = 10^(-6/20) * bus * 10^(Voice_Dry/20)              # VOICE DRY BUS -> Master Dry
wet = 10^(MasterWet/20) * ( 10^(DryLevel/2000) * bus + reverb(bus) )
out = clamp( limiter( masterEQ( dry + wet ) ), -1, 1 )
```

The two coherent dry copies alone sum to `0.50 + 0.63..0.84`, i.e. **+1.1..+2.6 dB** relative to
`bus` — the game's own arithmetic, not a mod bug. But note the `src` line: indoors it takes
**−6 dB** back off before any of that (section 5.1b), and the mod does not apply it. Net, indoors,
the mod's Local Voice is roughly 6 dB hotter than what a peer records; outdoors the two agree.

---

## 3. Every exposed AudioMixer parameter, its asset default and its writers

Read from the 38 `AudioMixerController` assets (`m_MixerConstant.exposedParameterNames` as CRC32,
`exposedParameterIndices` into the single snapshot's `values` table). 218 exposed parameters exist
across all mixers; the table below is complete for every mixer a voice can pass through.

### 3.1 `main mixer` (asset path id 156) — 58 exposed

| parameter | default | lands on | written by |
|---|---|---|---|
| `Voice_Dry` | 0 | `VOICE DRY BUS` volume | `GlobalAudioEffects.Update` (RVA `0x431EE0`) |
| `Voice_Wet` | 0 | `VOICE WET BUS` volume | `GlobalAudioEffects.Update` (same value as `Voice_Dry`) |
| `Voice_SuperWet` | 0 | `VOICE SUPER WET BUS` volume | `GlobalAudioEffects.Update` |
| `SFX_Dry` / `SFX_Wet` / `SFX_SuperWet` | 0 / 0 / 0 | the three SFX buses | `GlobalAudioEffects.SetSFXBusVol` (RVA `0x433070`), all three to the same value |
| `Music3DVol` | 0 | `MUSIC 3D BUS` volume | `GlobalAudioEffects.SetMusic3DVol` (RVA `0x433110`), `Update` |
| `MusicVol` *(CRC 2545798526; name recovered by brute force, no code writes it)* | 0 | `Music 3D Fixed Speakers` volume | nothing |
| `MasterWet` | **0** | `Master Wet` volume (the environment-reverb return) | `GlobalAudioEffects.Update` (`sp^10 · −80`), `ResetAll` (0) |
| `MasterLP` | **22000** | `Master Group` Lowpass Simple cutoff | `SetBlindfold` (5000 / 22000), `SetHeadphone` (16000 / 22000) |
| `MasterFreqGain3k` | **1** | `Master Group` ParamEQ 3300 Hz (octave 0.7) gain | `SetHeadphone` (0.5 / 1) |
| `MasterFreqGain1k` | **1** | `Master Group` ParamEQ 1000 Hz (octave 0.7) gain | `SetHeadphone` (1.5 / 1) |
| `MasterLimiterThreshold` | **−3** | `Master` Duck Volume threshold | `GlobalAudioEffects.SetMasterLimiterThreshold` (RVA `0x433230`) — **no compiled call sites**, so the asset default stands |
| `SuperWetPitch` | 1 | `Master Super Wet` **group pitch** (a resample: pitch *and* tempo) | `GlobalAudioEffects.Update`, `ResetAll` |
| `SuperWet_Speechlessness` | **−80** | `Speechlessness` return volume | `Update` (`(1 − sp^0.4)·−80`), `ResetAll` (−80) |
| `SuperWet_Blindfold` | **−80** | `Blindfold` return volume | `SetBlindfold` (−21 / −80) |
| `SuperWet_Ending` | **−80** | `Ending` return volume | `EndingTransition.AudioTransitionUpdate` (RVA `0x4B1EC0`) and `ResetAudio`, both inline; `ResetAll` (−80). `GlobalAudioEffects.SetSuperWetEnding` has **no callers** |
| `SuperWet_BlackTower` | **−80** | `BlackTower` return volume | `AlternativeInteriorAmb.Update` (RVA `0x331CC0`) → `SetSuperWetBlackTower(v·0.35)`; `OnDisable` → −80. Never touched by `ResetAll` |
| `PropPitch` | 1 | `Prop` **group pitch** | `Update` (`1 − sp·SpeechlessPitchDeduction`), `ResetAll` |
| `FoleyPitch` | 1 | `Foley` **group pitch** | only `ResetAll` (→ 1); no code path ever lowers it |
| `BiomeAmbPitch` | 1 | `Biome` **group pitch** | `Update` (`1 − sp·SpeechlessPitchDeduction·4.5`), `ResetAll` |
| `BiomeAmbLP` | 22000 | `Biome` Lowpass Simple | `AmbiencePlayer` |
| `OceanAmbVol` | 0 | `Ocean Ambience` volume | `SetBlindfold` (−15 / 0) |
| `AmbExtVol` | 0 | `Exterior Ambience` volume | `SetBlindfold` (−15 / 0) |
| `OceanAmbHP` / `AmbExtHP` / `AmbExtLP` | 0 / 0 / 0 | sends into `Environment HP` / `Environment LP` | `AmbiencePlayer`, `AlternativeInteriorAmb` |
| `FootstepVol` / `FootstepDry` / `FootstepSuperWet` | 0 / −3 / 0 | `Footsteps` volume and two sends | `GlobalAudioEffects.Update` (from `adr.Outdoorness` / `adr.ReverbTime`) |
| `DryLevel` `Room` `RoomHF` `RoomLF` `DecayTime` `DecayHFRatio` `Reflections` `ReflectDelay` `Reverb` `ReverbDelay` `HFReference` `LFReference` `Diffusion` `Density` | 0, −10000, 0, 0, 1, 0.5, −10000, 0.02, 0, 0.04, 5000, 250, 100, 100 (Unity's "off" preset) | `Master Wet` SFX Reverb | `AudioDynamicReverb.UpdateReverb` (RVA `0x508180`) or `AudioBasicReverb.UpdateReverb` (RVA `0x5026D0`), every `LateUpdate` |
| `WET_DryLevel … WET_LFReference` (14) | 0, −1677, −5000, 6.8, 0.15, −895, 0.01, −700, 0, 75, 54, 5000, −723, 250 | `Master Faint Wet` SFX Reverb | `SelfReverb.Update` (RVA `0x443160`) — **self-voice monitor only, never a remote voice** |

### 3.2 `voice mixer` (asset 4531) — the 48 per-channel floats plus `VoicePitch`

| parameter | default | lands on | written by |
|---|---|---|---|
| `Dry1`..`Dry12` | −80 | `Raw/n` group volume | `PlayerVoicePlaybackControl.Update` (RVA `0x3AED10`), `PlayVoice` (RVA `0x3AFE90`, → −80) |
| `High1`..`High12` | 0 | post-fader send `Raw/n` → `High` (Highpass Simple 250 Hz) | `PlayerVoicePlaybackControl.Update` |
| `ReverbFallWet1..12` | −80 | pre-fader send `Raw/n` → `Reverb Fall` | `Update`, `PlayVoice` (→ −80) |
| `ReverbBoostWet1..12` | −80 | pre-fader send `Raw/n` → `Reverb Boost` | `Update` |
| `VoicePitch` | 1 | `Dry` group Pitch Shifter pitch | `GlobalAudioEffects.Update` (`1 − sp·SpeechlessPitchDeduction`), `ResetAll` |
| `Room RoomHF RoomLF DecayTime DecayHFRatio Reflections ReflectDelay Reverb ReverbDelay HFReference LFReference Diffusion Density` (13; **no `DryLevel`**) | 0, −3000, −4000, 4, 2, −2000, 0.3, −1500, 0.1, 5000, 250, 100, 100 | `Reverb Boost` SFX Reverb (its `DryLevel` stays fixed at −10000) | `GlobalAudioEffects.Update` copies the environment reverb's 13, one frame late |

The `Reverb Fall` return's SFX Reverb has **no exposed parameters**: `DryLevel −10000, Room 0,
RoomHF −3000, DecayTime 4.0, DecayHFRatio 2.0, Reflections −2000, ReflectDelay 0.3, Reverb −1500,
ReverbDelay 0.1, Diffusion 100, Density 100, HFReference 5000, RoomLF −4000, LFReference 250`.
`DecayHFRatio 2.0` means the highs ring *twice as long* as the mids — a bright, long, late-heavy
tail.

### 3.3 `megaphone mixer 1..4` (assets 4518-4521) — 14 exposed each, identical

Chain, in order (fixed values are asset data, not written by code):

```
Attenuation
Lowpass Simple    5000 Hz                                   FIXED
ParamEQ           2500 Hz, octave 0.8, gain 2.5             FIXED
Pitch Shifter     <MegaphonePitch>=1, FFT 1024, overlap 4
Compressor        Threshold <CompressorThreshold>=-20, Attack 10 ms,
                  Release 1000 ms, MakeUpGain <CompressorGain>=13
SFX Reverb        DryLevel <ReverbDry>=-10000, Room <ReverbWet>=-1000,
                  RoomHF -2000, DecayTime <ReverbDecayTime>=6,
                  DecayHFRatio <ReverbDecayHFRatio>=0.1, Reflections -10000,
                  ReflectDelay 0, Reverb 0, ReverbDelay 0.04, Diffusion 100,
                  Density <ReverbDensity>=0, HFReference 3500,
                  RoomLF <ReverbLF>=0, LFReference 1000
Echo              Delay 100 ms, DecayRatio 0.3, MaxChannels 2,
                  DryMix 0, WetMix 1                        FIXED
Highpass Simple   <HPFrequency>=10
Lowpass Simple    <LPFrequency>=22000
Send -> own Duck  0 dB
Duck Volume       Threshold <PostCompressorThreshold>=0, Ratio 5, Attack 0,
                  Release <PostCompressorRelease>=0.125,
                  MakeUpGain <PostCompressorGain>=0, Knee 10, Sidechain 1
```

All 13 exposed floats are written every frame by `VoicePlayer.Update` (RVA `0x44BDC0`); see 5.9.

### 3.4 `megaphone master` (asset 4517) — 8 exposed

```
Master (0 dB)
 +- Parent (-80 dB)               <- the four megaphone mixers output here
 |    +- 1..4  (0 dB)  Send -> Dry <Megaphone{i}Dry>=0
 |                     Send -> Wet <Megaphone{i}Wet>=0
 |- Dry (0 dB)  Receive
 +- Wet (0 dB)  Receive · Attenuation · SFX Reverb
                { DryLevel -1000, Room 0, RoomHF -1000, DecayTime 12 s,
                  DecayHFRatio 2, Reflections -2000, ReflectDelay 0.3,
                  Reverb 0, ReverbDelay 0.1, Diffusion 100, Density 100,
                  HFReference 3000, RoomLF -2000, LFReference 250 }   FIXED
```

`Megaphone{i}Dry` / `Megaphone{i}Wet` are written by `VoicePlayer.Update`.

### 3.5 `radio mixer` (4527) and `walkietalkie mixer 1..10` (4532-4541) — 1 exposed each

Identical chains:

```
Attenuation
ParamEQ           5000 Hz, octave 0.25, gain 0.3            FIXED
Lowpass Simple    9000 Hz                                   FIXED
Highpass Simple   400 Hz                                    FIXED
ParamEQ           1500 Hz, octave 0.8, gain 3               FIXED
Compressor        Threshold -25, Attack 15 ms, Release 1000 ms, MakeUp 3   FIXED
Lowpass Simple    <WalkieTalkieLP>=22000                    <- VoicePlayer.Update, water depth
```

All of them output into `walkietalkie mixer` (4542), whose only effect is
`Pitch Shifter <WalkieTalkiePitch>=1` (written by `GlobalAudioEffects.Update`), which then outputs
into the main mixer's `Walkie Talkie` group.

### 3.6 `echo mixer` (4511) — 7 exposed (the cliff echo)

```
Master  vol <MasterVol>=0
        Attenuation
        SFX Reverb { DryLevel -10000, Room 0, RoomHF -3000, DecayTime 6 s,
                     DecayHFRatio 2, Reflections -2000, ReflectDelay 0.3,
                     Reverb -2000, ReverbDelay 0.1, Diffusion 100, Density 100,
                     HFReference 5000, RoomLF -2000, LFReference 250 }   FIXED
 |- local left / local center / local right   (0 dB each)
 |     Attenuation · Receive
 |     Lowpass Simple 3000 Hz                                     FIXED
 |     Highpass Simple 250 Hz                                     FIXED
 |     Send -> own Duck 0 dB
 |     Duck Volume { Threshold -25, Ratio 4, Attack 0.015, Release 0.125,
 |                   MakeUp 0, Knee 20, Sidechain 1 }              FIXED
 |     Echo { Delay <{Left,Center,Right}Delay>=1000 ms,
 |            DecayRatio <{Left,Center,Right}Decay>=0.5,
 |            MaxChannels 2, DryMix 0, WetMix 1 }
 |- remote  (0 dB)   same chain but with a FIXED Echo (1000 ms, 0.5)
 +- remote close (-80 dB)  Send -> local left / center / right, -6 dB each
```

`MasterVol`, `{Left,Center,Right}Delay` and `{Left,Center,Right}Decay` are written by
`SelfEcho.LateUpdate` (RVA `0x4422C0`) from the listener's own echo raycasts.

### 3.7 Other mixers a voice never reaches

`self voice mixer` (4529: LP 5000, HP 250, Compressor −25 / 50 ms / 1000 ms / +6) and
`pitch detector mixer` (4525, `Master` at −80 dB) both output into the main mixer's `Self Voice`
group, which sends only into `Master Faint Wet`. That is the game's own self-monitor
(`LocalVoicePlayer`), not a remote voice. `biome`, `foley`, `footstep`, `foliage rustle`,
`foliage windy`, `impacts`, `music 2D`, `music 3D`, `music radio`, `prop`, `scatter`,
`stage speaker`, `train horn 1..3 / test` carry no voice.

---

## 4. AudioMixerSnapshots

**There are none in any meaningful sense, and no snapshot API is ever called.**

- Every one of the 38 `AudioMixerController` assets has exactly **one** `AudioMixerSnapshotController`,
  all named `Snapshot`, and each is both `m_Snapshots[0]` and `m_StartSnapshot`. There is no second
  snapshot to transition to anywhere in the build.
- `AudioMixerSnapshot.TransitionTo`, `AudioMixer.TransitionToSnapshots` and `AudioMixer.FindSnapshot`
  have **zero** callers across `Assembly-CSharp` and `AudioSystem` (grepped over the
  `dll_il_recovery` decompilation, which carries the full call graph as attributes).
- Consequently the single snapshot's `values` table is only the **asset defaults** — which is
  exactly what section 3 reports. Everything dynamic in this game is an `AudioMixer.SetFloat`.

The complete set of `AudioMixer.SetFloat` call sites, from an ISIL-wide sweep with the literal in
`rdx` back-resolved:

| caller (RVA) | mixer | parameters | can it reach a remote voice? |
|---|---|---|---|
| `AudioDynamicReverb.UpdateReverb` (`0x508180`) / `AudioBasicReverb.UpdateReverb` (`0x5026D0`) | main | the 14 `DryLevel..Density` | **yes** — the environment reverb |
| `GlobalAudioEffects.Update` (`0x431EE0`) | main, voice, walkie, megaphone×4, impacts, stage speaker, train | see section 3 | **yes** |
| `GlobalAudioEffects.ResetAll` (`0x432AC0`) | main, voice | see below | **yes** |
| `GlobalAudioEffects.SetBlindfold` (`0x432C90`) | main | `MasterLP`, `SuperWet_Blindfold`, `OceanAmbVol`, `AmbExtVol` | **yes** |
| `GlobalAudioEffects.SetHeadphone` (`0x432E90`) | main | `MasterLP`, `MasterFreqGain3k`, `MasterFreqGain1k` | **yes** |
| `GlobalAudioEffects.SetSFXBusVol` (`0x433070`) | main | `SFX_Dry`, `SFX_Wet`, `SFX_SuperWet` | no (SFX buses) |
| `GlobalAudioEffects.SetMusic3DVol` (`0x433110`) | main | `Music3DVol` | no |
| `GlobalAudioEffects.SetSuperWetBlackTower` (`0x4331D0`) | main | `SuperWet_BlackTower` | **yes** (voice feeds that return) |
| `GlobalAudioEffects.SetSuperWetEnding` (`0x433170`), `SetMasterLimiterThreshold` (`0x433230`), `ResetSpeechlessness` (`0x432F70`) | main | — | **no compiled call sites** (inlined elsewhere or UnityEvent-wired) |
| `PlayerVoicePlaybackControl.Update` (`0x3AED10`) / `PlayVoice` (`0x3AFE90`) | voice | `Dry{n}`, `High{n}`, `ReverbFallWet{n}`, `ReverbBoostWet{n}` | **yes** — the per-voice channel |
| `VoiceSimulator.Update` (`0x44DD60`) / `OnEnable` (`0x44D710`) | voice | the same four, on `_index` parsed from its GameObject name | **yes**, and it will fight a real speaker on that slot |
| `VoicePlayer.Update` (`0x44BDC0`) | megaphone×4, megaphone master, radio/walkie | the 13 megaphone floats, `Megaphone{i}Dry/Wet`, `WalkieTalkieLP` | **yes** — megaphone / walkie copies |
| `SelfEcho.LateUpdate` (`0x4422C0`) | echo | `{Center,Left,Right}{Delay,Decay}`, `MasterVol` | **yes** — via `EchoRemote` (section 5.11) |
| `SelfReverb.Update` (`0x443160`) | main | the 14 `WET_*` | no — `Master Faint Wet` is self-voice only |
| `EndingTransition.AudioTransitionUpdate` (`0x4B1EC0`) / `ResetAudio` (`0x4B1CA0`) | main | `SuperWet_Ending` (+ `SetSFXBusVol`) | **yes** |
| `AlternativeInteriorAmb.Update` (`0x331CC0`) / `OnDisable` (`0x331BC0`) | main | `SuperWet_BlackTower` | **yes** |
| `PeckEffectHeadset.Peck` (`0x4DDA80`) | main | `MasterLP`, `MasterFreqGain3k`, `MasterFreqGain1k` (the "off" branch, inlined) | **yes** |
| `PeckEffectPipeVideoAudio.Update` (`0x4E0870`) | `bigScreenMixer` (serialized) | `BigScreenDryLevel` | no |
| `AmbiencePlayer.LateUpdate` (`0x333810`) | main | `OceanAmbHP`, `AmbExtHP`, `AmbExtLP`, `BiomeAmbLP` | no |
| `LoadingMenu.OnEnable` (`0x4C2DA0`) | main | `SetSFXBusVol(0)`, `Music3DVol = −80` | no |
| `StandaloneOcclusion.Update` (`0x445D80`) | serialized `Mixer` (`+0x38`) | a serialized parameter name (`FilterParam`, `+0x40`) × `MinGain` | **undetermined** — both the mixer and the name are scene data |

**Defaults written at startup.** `GlobalAudioEffects.Awake` (RVA `0x431A00`) writes no mixer
parameter itself; it builds the `BlindfoldLoop` `AudioEvent` and calls `ResetAll`. `ResetAll`
(RVA `0x432AC0`, only caller `WorldManager.OnDestroy` RVA `0x4A6200`) writes, in order:
`SetBlindfold(false)` (`MasterLP 22000`, `SuperWet_Blindfold −80`, `OceanAmbVol 0`,
`AmbExtVol 0`), then `MasterLP 22000`, `MasterFreqGain3k 1`, `MasterFreqGain1k 1`,
`VoicePitch 1` (voice mixer), `FoleyPitch 1`, `PropPitch 1`, `SuperWetPitch 1`,
`BiomeAmbPitch 1`, `SuperWet_Speechlessness −80`, `MasterWet 0`, `SuperWet_Ending −80`, and the
properties `VoiceNormalVol = 1`, `VoiceSuperWetVol = 1`. Everything else — `MasterLimiterThreshold`,
`SuperWet_BlackTower`, all `Voice_*` and `SFX_*` bus levels, `Footstep*`, `WalkieTalkiePitch`,
`MegaphonePitch`, and all 48 per-channel floats — keeps its **asset default** (section 3) until
something writes it.

---

## 5. Scenario detail

### 5.1 Baseline geometry — `Dry{n}`, `High{n}`, the 400 Hz EQ

`PlayerVoicePlaybackControl.Update` (RVA `0x3AED10`, 0x9EF bytes) runs every frame per remote
voice. Verified field reads, corrected against the DSP doc:

```
dist  = _sourceController.GetX(ListenerDistance = 0)
angle = _sourceController.GetX(Angle           = 120)
occl  = _sourceController.GetX(OcclusionLevel  =  70)
spatial = _sourceController._spatialBlend                            (ASC +0xC4)

playerCharacter.lips.audibility = _attenuation * _sourceController._finalVolume
                                  (lips is PlayerCharacter +0xA8; audibility is PlayerLips +0xC8;
                                   _finalVolume is ASC +0x84)

a = FilterAngleCurve.Evaluate(|angle|)        (PVPC +0x48)
d = FilterDistanceCurve.Evaluate(dist)        (PVPC +0x40)
t = spatial * 0.5 + 0.5
_eqFilter._dryWet = clamp01( t * (1 - (1-d)*(1-a)) )                 (BiquadFilters +0x34)

if (!TwoDMode) { _attenuation = min(SpatialVolCurve.Evaluate(spatial),
                                    AttenuationCurve.Evaluate(dist)) }
else           { _attenuation = 1; occl = 0; _fallWetLvl = 0 }

boost  = (1 - _attenuation)^3
       * adr.ReverbTime                       <-- (adr +0x38; NOT a "global voice volume")
       * clamp01((dist - 45) / -45)                          [45: 0x183976E88, -45: 0x183977214]
       * (1 - adr.Outdoorness)                                (adr +0x34)
       * (1 - occl)
       * clamp01((srcPos.y - listenerPos.y) / 30)             [30: 0x183976E58]

dryLin = max(_attenuation, boost/3 * heightFactor, 1e-4)              [3: 0x183976B88]
SetFloat(Dry{n},            max(-80, 20*log10(dryLin)))
SetFloat(ReverbFallWet{n},  20*log10(max(_fallWetLvl, 1e-4)))
SetFloat(ReverbBoostWet{n}, 20*log10(max(boost,       1e-4)))
SetFloat(High{n},           occl * -30)                               [-30: 0x183977208]
```

`High{n}` is the level of a **250 Hz high band** (`Highpass Simple 250 Hz` on the `High` group),
summed with a fixed 0 dB **250 Hz low band** (`Lowpass Simple 250 Hz` on `Low`). Occlusion
therefore attenuates only content above ~250 Hz by `occl · 30 dB`; it is not a shelf, and it is
not at 3 kHz.

**Where the three RTPC axes come from** (`AudioSourceController.GetX`; the per-frame driver is
`AudioManager.LateUpdate` RVA `0x517CA0` → its `_lateUpdateSet` sweep → `AudioSourceController.DoUpdate`
RVA `0x52E920`, which is also what calls `IAudioFilter.UpdateVariables(dt)` — that closes the DSP
doc's open inference about who drives `BiquadFilters`' coefficient recompute):

| axis | value | driven by |
|---|---|---|
| `ListenerDistance` (0) | `Vector3.Distance(listener, source)` | geometry |
| `Angle` (120) | `clamp01((dot(speakerForward, dirToListener) − 1) · −0.5) · 180` — a **linear** map, not `acos` | **the speaker's facing**: 0 when the speaker looks straight at you, 180 when they face away |
| `OcclusionLevel` (70) | `_occlusion.OccLvl` | listener-side raycasts |

The `Angle` term is worth noting for the Self-Ear: it is *speaker-side*, so the mod's open
calibration question ("one's own voice reaches one's ears off-axis") is precisely a question of
what `Angle` a peer standing in front of you measures — 0 if they are dead ahead of your facing,
180 if behind you. It feeds only the 400 Hz EQ's `DryWet` through `FilterAngleCurve`.

**Occlusion** (`AudioOcclusion`, AudioSystem): `_updateRate·4 + 1` rays per update between the
listener's `RandomPointsCenter` (jittered within `distance/6`) and the source; each hit contributes
`max(AudioMaterialConfig.OcclusionLookup[material])`; a sliding-window mean gives `_avg`; on a
clear line of sight the direct ray scales by `1 − (1 − d/max)^4` and is capped at
`CLEAR_PATH_MAX_LVL = 0.4`; finally `OccLvl = clamp01(Lerp(OccLvl, _avg, dt·5))`.
`AudioOcclusionBasic` is the cheap variant and lerps at 10/s. Besides `High{n}`, occlusion is also
written into the source's `BiquadFilters._gain` as `clamp(OccLvl · FilterMinGain, −30, +30)` dB
*(reported from `AudioOcclusion`; on a voice source this would collide with
`PlayerVoicePlaybackControl.PlayVoice`'s fixed `_eqFilter._gain = 30`, so which `BiquadFilters` it
targets is worth re-checking before relying on it)*.

**Voice blocking** is a separate quantity from occlusion: `AudioOcclusionBase.VoiceBlockingLvl` is
a per-hit membership test against `AudioMaterialConfig.VoiceBlockingMaterialsHashset`, averaged and
lerped the same way. It is consumed **only** through `GetX(VoiceBlock)` and therefore only by
whatever RTPC curves the Cue asset defines — data, not code. `PlayerVoicePlaybackControl.GetX`
forces it to 0 when the **local** player is `isGhost && !isAudioGhost`. The
`PlayerVoicePlaybackControl.voiceBlockingMask` field (`+0x30`) is dead, and
`SpeechlessTextOcclusion.VoiceBlockingLvl` returns a constant 0.

### 5.1b Indoor voice attenuation — `_outdoornessVol`

Easy to miss because it is not a mixer float: `PlayVoice` (RVA `0x3AFE90`) adds two `AudioVolume`s
to the pooled controller's volume chain, `_outdoornessVol` (PVPC `+0xA0`) and `_speechlessVol`
(`+0xB0`). Every frame `Update` writes:

```
outdoor = AudioManager.Instance.AudioDynamicReverb != null
        ? AudioDynamicReverb.Outdoorness            (adr +0x34)
        : 1.0
_outdoornessVol.Volume += ((outdoor * 0.5 + 0.5) - _outdoornessVol.Volume) * clamp01(dt * 3)
                                                                            [3: 0x183976B88]
```

so a remote voice is scaled by **0.5 (−6.02 dB) when the listener is fully indoors** and by 1.0
outdoors, smoothed at 3/s.

`AudioSourceController` then sets the Unity source's volume as

```
AudioSource.volume = ScriptableVolume > -2
                   ? clamp01(_audioSettingsVol.Volume * ScriptableVolume)     // override path
                   : clamp01( product of IAudioVolume.Volume over _chainOfVolume )  // CalculateVolume, RVA 0x533D90
AudioSource.pitch  = (ScriptablePitch > -2 ? max(0.1, ScriptablePitch) : _pitch) * SyncPitchMultiplier
```

so `_outdoornessVol` and `_speechlessVol` are plain multiplicative factors on the source's gain
envelope, applied *before* everything in section 2 — including the reverb sends, since `Dry{n}`
and both sends act on the already attenuated buffer.

**Two consequences.** (1) `minDistance` / `maxDistance` / `rolloffMode` are never written by code —
they are prefab data on the Cue, so the distance curve itself is not recoverable from the binary
(the mod does not need it: the Self-Ear is at distance 0). (2) The `ScriptableVolume > -2` branch
**bypasses the whole `_chainOfVolume`**, so in `TwoDMode` (ghost / 2D voice, section 5.12) the
indoor attenuation and the speechlessness volume are both skipped and the source runs at
`_audioSettingsVol · 1`. That is another reason `TwoDMode` is the game's own "unity-gain Self-Ear".

This is the same `Outdoorness` the mod already reads for the environment reverb, so it is one
multiply. It is also the most likely single explanation for the mod's Local Voice sitting hot
indoors relative to what a peer records.

`_amplitudeVol` (`+0xA8`) gets `1 − (1 − spc._outputArv)^5` written every frame too, but it is
only *added* to the chain by `PlayGibberish`, never by `PlayVoice`, so it is inert in normal play.

### 5.2 Fall reverb — `ReverbFallWet{n}`

```
wetTarget   = playerCharacter.faller.isInDanger ? playerCharacter.playerNetworking.outdoorness : 0
              (PlayerFaller +0x44 <isInDanger>; PlayerNetworking +0x14C outdoorness)
_fallWetLvl = wetTarget > _fallWetLvl ? wetTarget                      # instant rise
                                      : Lerp(_fallWetLvl, wetTarget, dt)   # ~1 s decay
```

Pre-fader, so `Dry{n}` does not scale it: a falling player's voice keeps its reverb tail even at
`Dry{n} = −80`. The return is `Reverb Fall` at **+3 dB** with the fixed 4 s reverb of 3.2.
Purely **speaker-side**, so TravelEar can and does evaluate it for the local player.

### 5.3 Reverb boost — `ReverbBoostWet{n}`

Formula in 5.1. It is non-zero only when the speaker is *attenuated* (`(1 − att)^3`), *above* the
listener (height factor), *indoors* (`1 − Outdoorness`), *unoccluded*, *within 45 m*, and the room
is *reflective* (`adr.ReverbTime`). At the Self-Ear `att = 1`, so it is identically zero — but the
mod's `MixerStageInputs.GlobalVoiceVolume` is a mis-port of `adr.ReverbTime` and should be renamed
before anything ever feeds it a real value.

The return is `Reverb Boost` at 0 dB, whose SFX Reverb keeps `DryLevel = −10000` (fully wet) and
mirrors the environment reverb's other 13 parameters one frame late (`GlobalAudioEffects.Update`).

### 5.4 Environment reverb

Unchanged from [`big-walk-environment-reverb.md`](big-walk-environment-reverb.md) section 1, and
now with the asset defaults confirmed (section 3.1): `MasterWet`'s asset default really is
**0 dB**, which closes that doc's open question. The 14 `DSP_*` values are readable as public
getters on `AudioManager.Instance.AudioDynamicReverb`.

### 5.5 The red bells — `SpeechlessZone` / `PlayerSpeechless`

This is the operator's "voices distort low and slow down near the bells, then go silent".

**The scalar.** `PlayerSpeechless` (a plain class on `PlayerCharacter +0x188`, field
`_speechlessness` at `+0x1C`) is ticked from `PlayerCharacter.Update` **for every player character
on every client**, so a remote speaker's `sp` is computed locally by each listener:

```
PlayerSpeechless.Update()                                            RVA 0x38BA50
  z = _speechlessZone;  if (z == null || destroyed) return;
  dist = |z.transform.position - playerCharacter.transform.position|
  sp   = (z.outerRadius == z.innerRadius) ? 0
       : clamp01( (dist - z.outerRadius) / (z.innerRadius - z.outerRadius) )
  speechlessness = sp
  // then a purely visual pass: Shader.SetGlobalFloat("SpeechlessModulation",
  //   max over the players in the zone of lips.amplitude)
```

`innerRadius < outerRadius`, so `sp` is **0 at or beyond `outerRadius` and 1 at or inside
`innerRadius`**, linear in distance between them. `PlayerSpeechless.GetDepth` (RVA `0x38BD50`) is
the same formula for an arbitrary sample point.

`_speechlessZone` is set by `SpeechlessZone.OnEnter(pc)` (RVA `0x3DC7F0`,
`pc.speechless.speechlessZone = this`) and cleared by `OnExit` (RVA `0x3DC820`, only if it is
still this zone). Those are wired to a `PlayerZone` physics trigger (`SpeechlessZone.playerZone`
`+0x28`, `noVisualZone` `+0x30`). `outerRadius` (`+0x20`) and `innerRadius` (`+0x24`) are
*(prefab/scene data)* but are **public fields**, readable at runtime.

Setting `speechlessZone` to null snaps `speechlessness` to 0 with no fade
(`set_speechlessZone`, RVA `0x38B6E0`, calls `set_speechlessness(0)`); leaving the trigger is
therefore instantaneous, but the trigger volume is normally larger than `outerRadius`, where `sp`
is already 0.

**What it does to a voice — five separate effects.** One uses the *speaker's* `sp`, four use the
*listener's*. All constants verified at the VAs given.

1. **Voice volume (speaker-side).** `PlayerVoicePlaybackControl.Update`:
   ```
   _speechlessVol.Volume = Mathf.Lerp(_speechlessVol.Volume,
                                      1 - playerCharacter.speechless._speechlessness,
                                      dt * 5)                          [5: 0x183976C80]
   ```
   `_speechlessVol` (PVPC `+0xB0`) is an `AudioVolume` (`_realtimeVolume` at `+0x14`) in the
   pooled `AudioSourceController`'s volume chain, so it is a plain linear gain applied *before*
   any of the DSP above. At `sp = 1` the voice is silent. `VoicePlayer.Update`'s tail does the
   same for a Megaphone / Radio / WalkieTalkie voice from `_sourcePlayerCharacter`.

2. **Voice pitch (listener-side).** `GlobalAudioEffects.Update` reads
   `sp = WorldManager.localPlayerCharacter.speechless._speechlessness` and writes, on the **voice**
   mixer:
   ```
   VoicePitch = 1 - sp * SpeechlessPitchDeduction
   ```
   `SpeechlessPitchDeduction` is `GlobalAudioEffects +0x80`, a public serialized float
   *(prefab data — not readable from the binary, readable at runtime)*. This lands on the voice
   mixer's `Dry` group **Pitch Shifter** (FFT 1024, overlap 4), which shifts pitch **without**
   changing tempo. The same value goes to `WalkieTalkiePitch` (walkietalkie mixer) and
   `MegaphonePitch` (all four megaphone mixers) — also Pitch Shifters.

3. **Everything-else pitch (listener-side).** The same `Update` writes, on the **main** mixer:
   ```
   PropPitch     = 1 - sp * SpeechlessPitchDeduction          -> Prop  group PITCH
   BiomeAmbPitch = 1 - sp * SpeechlessPitchDeduction * 4.5    -> Biome group PITCH   [4.5: 0x183976C78]
   SuperWetPitch = 1 - sp^0.4 * MusicPitchDeduction           -> Master Super Wet group PITCH
                                                                 [0.4: 0x18397672C]
   ```
   These are **AudioMixerGroup pitch**, i.e. a resample: lower *and slower*. That is the exact
   difference the operator heard — the ambience, the radio and the props slow down, the voices
   only drop in pitch. `MusicPitchDeduction` is `GlobalAudioEffects +0x84` *(prefab data)*.
   `FoleyPitch` is exposed and reset to 1 by `ResetSpeechlessness`, but `Update` never lowers it.

4. **Super-wet bloom (listener-side).**
   ```
   SuperWet_Speechlessness = (1 - sp^0.4) * -80                        [-80: 0x18397721C]
   ```
   | `sp` | `SuperWet_Speechlessness` |
   |---|---|
   | 0 | −80 dB (silent) |
   | 0.1 | −48.2 dB |
   | 0.25 | −34.1 dB |
   | 0.5 | −19.4 dB |
   | 0.75 | −8.7 dB |
   | 1 | 0 dB |

   Because the main mixer's `Voice` group already sends into `VOICE SUPER WET BUS` at **0 dB**
   unconditionally, opening this return routes a full-level copy of every remote voice into
   `Master Super Wet` — the fixed 6.8 s reverb + chorus of section 2 — pitched down by
   `SuperWetPitch`. This, not the pitch shifter, is the "distorted, smeared, slowed" character.

5. **Environment-reverb kill (listener-side).**
   ```
   MasterWet = sp^10 * -80                                             [10: 0x183976D54]
   ```
   sp 0.5 → −0.08 dB, 0.8 → −8.6 dB, 0.9 → −27.9 dB, 1 → −80 dB. The normal room reverb only
   disappears right at the centre of the zone.

`GlobalAudioEffects.ResetSpeechlessness` (RVA `0x432F70`) restores `VoicePitch`, `FoleyPitch`,
`PropPitch`, `SuperWetPitch`, `BiomeAmbPitch` to 1, `SuperWet_Speechlessness` to −80 and
`MasterWet` to 0 — but it has **no compiled call sites**; the same writes are inlined into
`ResetAll` (world teardown) and `Update` produces them naturally at `sp = 0`. Note also that the
whole `GlobalAudioEffects.Update` block from `VoicePitch` down to the `Reverb Boost` mirror is
**skipped entirely** when `WorldManager.localPlayerCharacter` is null, so all of these keep their
last values in menus.

**For the mod.** Everything needed is public and local. At the Self-Ear the local player is both
the speaker and the listener, so **all five terms apply at once** and all five read the same
scalar: `WorldManager.localPlayerCharacter.speechless.speechlessness` is exactly the `sp` a peer
computes for us (effect 1, up to position-interpolation error, since each client derives it from
the replicated transform rather than from a synced float) *and* the `sp` our own listener side
uses (effects 2-5). `GlobalAudioEffects.Instance.SpeechlessPitchDeduction` and
`.MusicPitchDeduction` are public fields. `MixerFloats` already sees `SuperWet_Speechlessness`,
`SuperWetPitch`, `VoicePitch` and `MasterWet` go by as `SetFloat` writes, so the mod can drive the
listener-side four straight off the game's own numbers and only needs the scalar for effect 1.

### 5.6 Blindfold — one peck, two completely different effects

`PeckEffectMask.Peck(PeckContext)` (RVA `0x4DE9B0`) runs on **every** client for **every** peck,
and branches on whether the pecked player is that client's local player:

```
pc     = peckContext.playerIdentity.GetComponent<PlayerCharacter>();
active = peckContext.compressedState != 0;
if (pc.netIdentity.isLocalPlayer) {                     // the pecked player is ME
    switch (maskType /*PeckEffectMask +0x48*/) {
      0 Binoculars: WorldMenuManager.SetBinocularsMask(active)
      1 Telescope : WorldMenuManager.SetTelescopeMask(active)
      2 Blindfold : WorldManager.Instance.postProcessingManager.blindfoldPPVolume.weight = active ? 1 : 0;
                    GlobalAudioEffects.Instance.SetBlindfold(active);        // GLOBAL, listener-side
    }
} else {                                                // the pecked player is SOMEONE ELSE
    pc.lips.playerVoicePlaybackControl.SetBlindFoldMode(active);             // PER-VOICE, speaker-side
}
```

So the two mechanisms never coincide on one machine:

**(a) `GlobalAudioEffects.SetBlindfold(bool)` — RVA `0x432C90`, only on the wearer's own machine.**

| | active | inactive |
|---|---|---|
| `MasterLP` | 5000 `[0x183977024]` | 22000 `[0x183977054]` |
| `SuperWet_Blindfold` | −21 `[0x1839771F8]` | −80 `[0x18397721C]` |
| `OceanAmbVol` | −15 `[0x1839771EC]` | 0 |
| `AmbExtVol` | −15 | 0 |
| `BlindfoldLoop` | `AudioEvent.Play(fade −1)` | `AudioEvent.Stop(fade −1)` |

Both terms hit voices: the 5 kHz `MasterLP` is on `Master Group`, downstream of everything, and
`SuperWet_Blindfold` at −21 dB opens the same `Master Super Wet` reverb + chorus that speechlessness
uses, fed by `VOICE SUPER WET BUS` at 0 dB. So while **you** are blindfolded, every remote voice you
hear is low-passed at 5 kHz and gains a −21 dB, 6.8 s reverb-and-chorus halo.

**(b) `PlayerVoicePlaybackControl.SetBlindFoldMode(bool)` — RVA `0x3B0C60`, on everyone else's
machine, applied to the wearer's voice.** This is the one that matters for TravelEar: **when the
local player puts on a blindfold, every peer hears the local voice through a full-wet 1500 Hz
low-pass.**

```
SetBlindFoldMode(active):
    _blindFoldMode /*PVPC +0xE0*/ = active;
    if (active)  ApplyBlindFoldFilter();                          // tail-jump to 0x3B0E70
    else if (_blindFoldFilter != null) {
        _sourceController._filters.Remove(_blindFoldFilter);
        Object.Destroy(_blindFoldFilter);
        if (_filters.Count == 0) Object.Destroy(_sourceController._filterMixer);
        _blindFoldFilter = null;
    }

ApplyBlindFoldFilter():                                           // RVA 0x3B0E70
    if (!_blindFoldMode || _sourceController == null) return;
    f = _sourceController.gameObject.AddComponent<BiquadFilters>();
    f.Type = LowPass (1);
    _sourceController._filters.Add(f);
    ... ensure the AudioFilterMixer ...
    _blindFoldFilter = f;
    f._frequency = 1500f; f._dirty = true;
    f._q         = 0.6f;  f._dirty = true;
```

Two details that change the sound:

- **There is no `Bypass` write anywhere.** The filter is enabled by *existence* and disabled by
  *destruction*. The DSP doc's "`[Bypass unless blindfolded]`" is a fair description of the effect
  but not of the mechanism — when not blindfolded there is no third filter in the list at all.
- `BiquadFilters`'s constructor defaults leave `_vol = 1` and `_dryWet = 1`, and
  `ApplyBlindFoldFilter` overrides only `Type`, `_frequency` and `_q`. So the kernel output is
  `1·biquad(x) + 0·x` — a **fully wet** RBJ low-pass at 1500 Hz, Q 0.6, with the usual per-block
  coefficient ramp. It is not a subtle blend.

`ApplyBlindFoldFilter` is also called from `PlayVoice` (RVA `0x3AFE90`) and `PlayGibberish`
(RVA `0x3B0560`), because the filter lives on the pooled `AudioSourceController`'s GameObject and
must be rebuilt whenever the source is recycled.

`PeckEffectMask.SetMask(bool)` (RVA `0x4DE8F0`) and `PostProcessingManager.SetBlindfold(bool)`
(RVA `0x49F860`) contain the same logic but have **zero compiled call sites** — `Peck` inlines
them. `BlindfoldPopper` (`Launch`, RVA `0x467A60`), `GameStartBlind` and `EndingFadeBlind`
(`SetFade`, RVA `0x4AB3E0`) touch no audio at all; they are UGUI image fades and a server-side
launcher.

`VoicePlayer` has no blindfold handling of any kind (grepped case-insensitively over both its
diffcs and its ISIL): the megaphone / walkie / radio copies of a blindfolded player's voice are
**not** low-passed.

### 5.7 Headphone

`GlobalAudioEffects.SetHeadphone(bool)` (RVA `0x432E90`), main mixer only:

| | active | inactive |
|---|---|---|
| `MasterLP` | 16000 `[0x183977048]` | 22000 |
| `MasterFreqGain3k` | 0.5 `[0x183976768]` | 1 `[0x18397689C]` |
| `MasterFreqGain1k` | 1.5 `[0x183976950]` | 1 |

Those two ParamEQs sit at 3300 Hz and 1000 Hz, both octave 0.7, on `Master Group`. So the
"headphone" state is a −6 dB dip at 3.3 kHz, a +3.5 dB lift at 1 kHz and a 16 kHz lowpass on
everything, voices included.

The only caller is `PeckEffectHeadset.Peck` (RVA `0x4DDA80`). The ON branch is gated by
`pc.netIdentity.isLocalPlayer`; the OFF branch is gated by a machine-local
`localPlayerIsListening` flag (`+0xB8`) and **inlines** the three "off" `SetFloat`s rather than
calling `SetHeadphone(false)`. It also activates a `listenerMover` GameObject (physically moving
the `AudioListener`) and adds/removes a Dissonance token (`+0xB0`), so wearing the headset changes
*which* voices reach you as well as how they sound.

`PeckEffectPipeVideoAudio.Update` (RVA `0x4E0870`) is **not** a headphone caller: it writes
`BigScreenDryLevel = clamp01(d/100) · −1200` on its own serialized `bigScreenMixer` (`+0x38`), a
mixer no voice passes through. `[100: 0x183976EEC, −1200: 0x183977260]`

### 5.8 Ending and black tower

**Ending.** `GlobalAudioEffects.SetSuperWetEnding` (RVA `0x433170`) has **zero callers**;
`EndingTransition.AudioTransitionUpdate(float t)` (RVA `0x4B1EC0`) writes the parameter inline.
`EndingTransition.SetActive` (RVA `0x4B1370`) records `_transitionStartTime = Time.time`; `Update`
(RVA `0x4B1660`) then evaluates `t = (Time.time − _transitionStartTime) / duration` and drives:

| write | from |
|---|---|
| `GlobalAudioEffects.VoiceSptialBlend` (`+0xCC`) | `voiceSpatialCurve` (`+0x48`) |
| `MenuAudio` volume | `menuAmbFadeinCurve` (`+0x40`) |
| `SetSFXBusVol(v)` → `SFX_Dry/Wet/SuperWet` | `gameAudioFadeoutCurve` (`+0x38`) |
| `GlobalAudioEffects.VoiceNormalVol` (`+0xC4`) | `voiceFadeoutCurve` (`+0x50`) |
| `GlobalAudioEffects.VoiceSuperWetVol` (`+0xC8`) | `voiceWetFadeoutCurve` (`+0x58`) |
| **`Mixer.SetFloat("SuperWet_Ending", 20·log10(max(v,1e−4)))`** | `voiceReverbLvlCurve` (`+0x60`) |
| RTPC-X key 110 on `goodbyeMusicRTPCX` | `musicFadeoutCurve` (`+0x68`) |

So the ending fades the *voice* down (`VoiceNormalVol` → `Voice_Dry`/`Voice_Wet`), fades the voice
*super-wet* up (`VoiceSuperWetVol` → `Voice_SuperWet`, plus `SuperWet_Ending`), and pushes voices
toward 2D (`VoiceSptialBlend`, read by `VoicePlayer.GetX` for `XAxisType 170`). All seven curves
and `duration` (`+0x20`) are prefab data. `ResetAudio` (RVA `0x4B1CA0`) restores
`VoiceSptialBlend = 1`, `VoiceNormalVol = 1`, `VoiceSuperWetVol = 1`, `SetSFXBusVol(1)`,
`SuperWet_Ending = −80`, RTPC 110 = 1; it is called from `OnDisable` and from an `AudioClock`
alarm set by `OnTransitionEnd` (RVA `0x4B1AB0`).

Started by `PeckEffectEndingTransition.OnPeck` (RVA `0x4DD9A0`) →
`WorldManager.Instance.worldMenuManager.secondEndingTransition.SetActive()`, or by
`AutomaticDisconnector.StartEndingTransition(PlayerCharacter)` (RVA `0x3D7960`).

**Black tower.** `GlobalAudioEffects.SetSuperWetBlackTower(v)` (RVA `0x4331D0`) has exactly one
caller, `AlternativeInteriorAmb.Update` (RVA `0x331CC0`), and it is a *volume follower* on an
ambience emitter, not a distance formula of its own:

```
v = _asc._attenuationVol.Volume * _asc._rtpcVol.Volume     // ASC +0x88 and +0x98
GenericAmbRTPCXProvider.X[key 150] = 1 - v
if (SuperWet /* serialized bool, +0x30 */)
    SetSuperWetBlackTower(v * 0.35f)                       // [0.35: 0x1839766F4]
      -> Mixer.SetFloat("SuperWet_BlackTower", 20*log10(max(v*0.35, 1e-4)))
```

`_attenuationVol` is the emitter's own 3D rolloff, so the falloff lives in the `AudioAsset` /
`AudioOcclusionConfig` (asset data). `OnDisable` (RVA `0x331BC0`) and the `OnEnable` clear-ref
(RVA `0x331ED0`) both reset it to −80 dB.

**Routing of the four super-wet returns** (from the mixer asset, section 3.1):

- `Ending` is fed **only** by `VOICE SUPER WET BUS` — a voice-only effect.
- `BlackTower` is fed by `VOICE SUPER WET BUS` **and** `SFX SUPER WET BUS`.
- `Speechlessness` is fed by voice, SFX **and** `MUSIC 3D BUS` at −18 dB.
- `Blindfold` is fed by voice, SFX and the `Biome` group.

All four then run the same fixed 6.8 s SFX Reverb + Chorus on `Master Super Wet` at group pitch
`SuperWetPitch`.

### 5.9 Megaphone

State machine: `RadioVoiceAssigner` (see the wiring doc section 4). Rendering: a `VoicePlayer`
with `PlayerType == Megaphone` on the prop, fed from the *same* `SamplePlaybackComponent` ring as
the direct voice — so a listener beside the holder hears **both** the direct voice and the
megaphone.

In-process (built once in `VoicePlayer.OnEnable`, RVA `0x44B200`):
`BitCrusher` (BitDepth 24, CrushRate 4800, DryWet 0.6, Smooth 0.6, Mono true) then a
`BiquadFilters` `HighPass 300 Hz Q 0.4`.

Every frame `VoicePlayer.Update` (RVA `0x44BDC0`) retunes the crusher and writes 13 floats on
`megaphone mixer {index}` plus 2 on `megaphone master`. With `d` the listener distance,
`t_k = clamp01(d/k)`, `ease(t) = (2−t)·t`, `e_k = ease(t_k)`:

**Remote holder** (the branch that matters for TravelEar, because it is what a peer hears):

```
bitcrusher (shared, before the branch):  t = clamp01((d-5)/295)
    DryWet = t*0.5 + 0.5 ;  CrushRate = (int)(t*4000 + 4000) ;  Smooth = t*0.5 + 0.5

occ  = AudioUtil.EaseInOut(GetX(OcclusionLevel), 0.5, t500*5 + 1)
prod = outdoorLocal * _sourcePlayerCharacter.playerNetworking.outdoorness
loud = (1 - (1 - 0.75*prod) * (0.75*occ)) * sqrt(1 - t600)
att  = 1 - loud

PostCompressorThreshold = -15 - att*45                    [-15: 0x1839771EC, 45: 0x183976E88]
PostCompressorRelease   = 2*att + 0.25                    [0.25: 0x18397669C]
PostCompressorGain      = 0
CompressorThreshold     = -25 - t500*25                   [-25: 0x1839771FC, 25: 0x183976E0C]
CompressorGain          = t400*15 + 6                     [15: 0x183976DB8, 6: 0x183976CD0]
ReverbDry               = e800 * -1350                    [-1350: 0x183977264]
ReverbWet               = e500*1000 - 1000                [1000: 0x183976FD8]
ReverbDecayTime         = e500*7 + 2                      [7: 0x183976D10, 2: 0x183976A84]
ReverbDecayHFRatio      = 0.5 - t100*0.4
ReverbDensity           = e300 * 40                       [40: 0x183976E78]
ReverbLF                = e150 * -1000
HPFrequency             = e300 * 500
LPFrequency             = 22000 - t100*19000              [19000: 0x18397704C]

megaphone master:  Megaphone{i}Dry = clamp01(d/600) * -12                [-12: 0x1839771E8]
                   Megaphone{i}Wet = 20*log10(max(clamp01(d/500), 1e-4))
```

**Local holder** (`_sourcePlayerCharacter.netIdentity.isLocalPlayer`): crusher DryWet 0.5,
CrushRate 4800, Smooth 0.5; `PostCompressorThreshold −40`, `PostCompressorRelease 0.125`,
`PostCompressorGain 6`; `CompressorThreshold −35`, `CompressorGain = outdoorLocal·3 + 6`;
`ReverbDry = roomSize·−1000`, `ReverbWet 0`, `ReverbDecayTime = roomSize^4·1.4 + 0.1`,
`ReverbDecayHFRatio = 1 − roomSize·0.5 + outdoorLocal·0.5`, `ReverbDensity 0`,
`ReverbLF = −250 − roomSize·750 − outdoorLocal·500`, `HPFrequency = roomSize·200 + 500`,
`LPFrequency 3500`; `Megaphone{i}Dry = 0`, `Megaphone{i}Wet = −80`.

**At the Self-Ear (`d = 0`, remote-holder branch)** — what a peer standing next to the local
megaphone user actually hears:

```
BitCrusher      24 bit, 4000 Hz hold, DryWet 0.5, Smooth 0.5
BiquadFilters   HighPass 300 Hz Q 0.4
megaphone mixer 1..4:
  Lowpass Simple   5000 Hz                                      (asset, always)
  ParamEQ          2500 Hz oct 0.8 gain 2.5                     (asset, always)
  Pitch Shifter    1.0
  Compressor       Threshold -25, Attack 10 ms, Release 1000 ms, MakeUp +6
  SFX Reverb       DryLevel 0 mB (full dry), Room -1000 mB (-10 dB wet),
                   DecayTime 2 s, DecayHFRatio 0.5, Density 0, RoomLF 0,
                   RoomHF -2000, Reflections -10000, ReflectDelay 0,
                   Reverb 0, ReverbDelay 0.04, Diffusion 100,
                   HFReference 3500, LFReference 1000
  Echo             100 ms, decay 0.3, DryMix 0, WetMix 1        (asset, always)
  Highpass Simple  0 Hz  (open)
  Lowpass Simple   22000 Hz (open)
  Duck Volume      Threshold -15, Ratio 5, Attack 0, Release 0.25 s, Knee 10
megaphone master:
  Megaphone{i}Dry = 0 dB ;  Megaphone{i}Wet = -80 dB     -> fully dry, no 12 s valley reverb
main mixer Megaphone group -> VOICE DRY -6 dB / VOICE WET 0 dB
```

The 12 s `megaphone master` `Wet` reverb only fades in with distance (`Megaphone{i}Wet` reaches
0 dB at `d ≥ 500 m` while `Dry` falls to −12 dB): close up the megaphone is dry, far away it is
the valley-wide roar.

### 5.10 Walkie-talkie / radio

`RadioVoiceAssigner` with `PlayerType == WalkieTalkie` or `Radio`. In process: only the
`BitCrusher` (24 bit / 4800 Hz / 0.6 / 0.6) — no high-pass, no per-type biquad. The colour comes
entirely from the mixer chain in 3.5 plus the `walkietalkie mixer` pitch shifter. Note the shared
`radio mixer`: several props can be on the same mixer at once, and `WalkieTalkieLP` is written by
whichever `VoicePlayer` runs last.

### 5.11 Cliff echo on a remote voice — `SelfEcho` / `EchoRemote`

`EchoRemote` lives on the same prefab as each remote `PlayerVoicePlaybackControl` and creates two
extra `VoicePlayer`s driven from the same `SamplePlaybackComponent`:

| player | position | volume | mixer group |
|---|---|---|---|
| `VoicePlayerDirect` | at the remote player | `_echoDirectVol.Volume = Lerp(v, (Amt[c]+Amt[l]+Amt[r])/3 · outdoorLocal · remote.playerNetworking.outdoorness, clamp01(dt·3))` | `echo mixer` → `remote` |
| `_voicePlayerClose` ("Echo Remote Close") | **parented under `SelfEcho`**, localPosition `(0,0,1)` — 1 m in front of *the listener* | `_echoCloseVol.Volume = Lerp(v, remote.playerNetworking.echoAmount · outdoorLocal, clamp01(dt·2))` | `echo mixer` → `remote close` |

*(Which of the two `VoicePlayer`s lands on which group is inferred from the group names — the Cue
assets and their `AudioBus.MixerGroup` were not read. The `echo mixer` group set is
`local left / local center / local right / remote / remote close`, and `SelfEcho`'s three
`LocalVoicePlayer` emitters take the three `local *` groups, so the two `EchoRemote` players must
take the remaining two.)*

`remote` gets a **fixed** 1000 ms / 0.5 echo; `remote close` is at −80 dB and sends −6 dB into the
listener's three directional emitters (`local left/center/right`), whose `Echo` delay and decay
`SelfEcho.LateUpdate` drives from the listener's own raycasts:

```
Delay(ms) = 700 + 800 * far/(mid+far)      (700 when there is no reflector)
Decay     = 0.4 + 0.2 * mid
Amount    = (1 - near) * (mid + far)
MasterVol = lerp(clamp01(heightOffTerrain/100) * -6 dB, rate 3/s)
```

Every echo copy also passes the `echo mixer`'s per-emitter LP 3000 / HP 250 / ducker, then the
mixer master's fixed 6 s SFX Reverb, then the main mixer's `Echo` group into `VOICE DRY BUS` at
0 dB — **dry only**, so the cliff echo never enters the environment reverb.

`echoAmount` and `outdoorness` are synced every 2 s by `SelfEcho` (`CmdSetEchoAmount`,
`CmdSetOutdoorness`), so the local player already publishes both values; the mod can read them
directly off `WorldManager.localPlayerCharacter.playerNetworking`.

### 5.12 Ghost and 2D voice — the game's own Self-Ear

`PlayerLips.SetGhost(bool)` (Assembly-CSharp) and `PlayerLips.Set2DVoice(bool)` both end in the
same two writes:

```
lips.playerVoicePlaybackControl.TwoDMode        = value       (PVPC +0xC4)
lips.playerVoicePlaybackControl._sourceController.<+0x129> = value
```

and `PlayerVoicePlaybackControl.Update`'s `TwoDMode` branch then produces
`ScriptableVolume = 1`, `ScriptableSpatialBlend = 0`, `_attenuation = 1`, `_fallWetLvl = 0`,
`occl = 0`, hence `Dry{n} = 0 dB`, `High{n} = 0 dB`, `ReverbFallWet{n} = ReverbBoostWet{n} = −80 dB`
— **exactly the values TravelEar's `MixerStageInputs.SelfEar` produces**. The game itself ships a
"render this voice as if it were at the listener" mode, and it agrees with the mod's model.

`ScriptableVolume = 1` additionally takes the source down the `ScriptableVolume > -2` branch of
`AudioSourceController`, which **bypasses `_chainOfVolume` entirely** (section 5.1b). So a ghost's
voice also loses the indoor −6 dB and the speechlessness gain — it is played flat at
`_audioSettingsVol`. That is the closest thing in the game to a reference "what the voice sounds
like with no listener geometry at all", and it is what TravelEar renders.

`SetGhost` also manipulates the Dissonance token set (`alive` / `ghost` / `echo`) and the
`VoiceBroadcastTrigger`'s token list. Triggers: `PlayerNetworking.isGhost` (SyncVar hook) and two
sites in `PlayerCharacter`. `is2DVoice` is a SyncVar with hook `OnSet2DVoice`; `CmdSet2DVoice` has
no in-code caller (section 10).

### 5.13 Mute, silence, gibberish

- **Self-mute** (`WorldManager.SetMuted` from `PlayerLips.Update`) is transmit-side: Dissonance
  stops sending. `PlayerLips.isMuted` reads `localIsMuted` for the local player and
  `playerNetworking.isMuted` (`+0x142`) for a remote; that flag drives `VoicePlayer.Update`'s
  `_muteVol` for Megaphone/Radio/WalkieTalkie players and `LocalVoicePlayer.LateUpdate`, but the
  normal `PlayerVoicePlaybackControl` path has no mute volume at all.
- **Moderation silence** (`PlayerLips.isSilenced`, set from `ModerationPlayerCard` /
  `ModerationSilenceConfirmMenu` / a `PlayerNetworking` hook) calls into the Dissonance
  `IDissonancePlayer` vtable — a local mute at the Dissonance level, so the voice never decodes.
  `PlayerLips.isContentRestricted` returns a hard `false` in this build.
- **Channel exhaustion.** `PlayerVoicePlaybackControl.TryTakeCue` (RVA `0x3AE120`) pops a
  `SoundCue` off the static `cueStack` (statics `+0x8`), reads `_index` by
  `int.Parse(cue.Bus.MixerGroup.name) − 1`, and returns false when the stack is empty, logging
  once (`_reportedNoCue`, `+0xC7`):
  `"can't get a voice cue from the stack for {0}! {1} voice controls are active but only {2} cues exist, so this player stays silent until one frees up."`
  `Update` returns immediately when `TrySetUp` fails, so the 13th concurrent speaker is simply
  **inaudible** — there is no fallback channel and no ducking of the others; the control retries
  every frame and logs once. A cue is held from the moment a player's control takes it until the
  control is disabled; the only release is `OnDisable` (RVA `0x3AEA40`), and there are two paths
  (an unusable cue, and a mixer-group name that does not parse to 1..12) on which a cue is never
  returned to the stack.
- **`VoiceSimulator`** (`OnEnable` RVA `0x44D710`, `Update` RVA `0x44DD60`) is a dev/test emitter
  that parses its GameObject name to an integer, sets `_index = n − 1` and then writes the same
  four `PlayerVoicePlaybackControl` static parameter arrays — `Dry{n}` and `ReverbBoostWet{n}` as
  `20·log10(max(v, 1e−4))`, `High{n}` as `x·−30`, `ReverbFallWet{n}` as a flat −80. It therefore
  occupies a real voice slot and will fight a live speaker assigned to the same channel. Not
  reachable in normal play, but it is the reason two things can write `Dry{n}`.
- **Gibberish** (`PlayerVoicePlaybackControl.GibberishMode`, a static) makes `TrySetUp` call
  `PlayGibberish` instead of `PlayVoice`: `SamplePlaybackComponent.AmplitudeOnlyMode = true` (the
  voice is measured, not played), an extra `_amplitudeVol =`
  `1 − (1 − spc._outputArv)^5` volume, and a separate `GibberishCue`.

### 5.14 Water

The **only** water-driven audio parameter in the voice path is `WalkieTalkieLP`, written by
`VoicePlayer.Update` for a WalkieTalkie player from **its own transform**, i.e. the *receiving*
radio prop in the local client's scene — not the speaker, and nothing about the speaker's depth is
networked:

```
depth = WaterDepthSampleData.GetDepth(this.transform.position)     // RVA 0x44E970
                                                                    // = worldPos.y - waterSurfaceHeight, negative when submerged
t  = clamp01(depth / -0.2)                                          [-0.2: 0x183977118]
t2 = clamp01( t / ((1-t)*0.5 + t) )                                 [0.5: 0x183976768]
WalkieTalkieLP = t2 * -21820 + 22000                                [-21820: 0x183977284, 22000: 0x183977054]
```

Range **22000 Hz → 180 Hz**, fully engaged once the radio is 0.2 m under. Because
`_walkietalkieMixer` is a whole mixer (not a per-source effect), several receiving radios in one
scene fight over the parameter and the last `Update` of the frame wins.

`WaterDepthSampleData.GetDepth` has exactly four callers in the whole binary — `VoicePlayer.Update`
(this), `MusicPlayer.ManualUpdate` (a volume duck), `FootstepSound.UpdateWaterValues` and
`CollisionSound.AudioFixedUpdate` (splash selection). `PlayerVoicePlaybackControl` is not among
them.

There is no listener-side underwater muffle. What was searched, and came back empty:

- `WaterDepthSampleData.GetDepth`'s complete caller set is `CollisionSound`, `FootstepSound`,
  `MusicPlayer` and `VoicePlayer` — only the last is in a voice path, and only for
  `WalkieTalkieLP`.
- No mixer parameter in section 3 is water-driven, and the only listener-global filters
  (`MasterLP`, the two ParamEQs) are written solely by `SetBlindfold` / `SetHeadphone`.
- Unity's own per-source filter wrappers (`AudioSourceController.AddUnityFilter`, wrapping
  `AudioLowPassFilter` / `AudioHighPassFilter` / `AudioReverbFilter` / `AudioEchoFilter` /
  `AudioChorusFilter` / `AudioDistortionFilter`) are used by exactly two classes, `AmbiencePlayer`
  and `AudioFilterTester` — never on a voice source.
- `AudioDynamicReverb` has no water term (its four scalars come from raycasts and `ReverbZone`
  overrides only).
- `AudioLowPassFilter` is referenced only by `AudioSourceRefs.LP` and `UnityFilterLP._filter`;
  `AudioLowPassFilter.set_cutoffFrequency` is called by **no game code**. `AudioListenerController`
  has no filter fields and no water references.
- `AudioSourceController.set_outputAudioMixerGroup` is never called, so a voice cannot be
  re-routed to a different (e.g. "underwater") mixer at runtime.
- The RTPC system cannot express it either: `XAxisType` has no water or depth axis and `YAxisType`
  has no filter output.
- Metadata greps for `Muffle`, `UnderwaterLP`, `WaterLP`, `Submerged`, `WaterMuffle`, `OceanLP`,
  `WaterCutoff`, `UnderwaterSnapshot` return nothing; every `Underwater*` string belongs to Crest's
  renderer. `PlayerGround.isSwimming` (`+0x100`) is read by no audio class.
- There is no snapshot to switch to (section 4).

A swimming listener hears remote voices unfiltered; a swimming *speaker* is likewise unaffected
except through the walkie-talkie prop.

### 5.15 Master limiter

The main mixer's `Master` group sends into itself at 0 dB and then runs a **Duck Volume** with
`Threshold = MasterLimiterThreshold` (asset default **−3 dB**), `Ratio 10`, `Attack 0`,
`Release 0.125 s`, `MakeUpGain 0`, `Knee 20`, `Sidechain 1`. Self-sidechained at −3 dB with 10:1
this is a brick-wall limiter on the whole mix, and with the +1..+2.6 dB the voice path already
carries (section 2) it engages on ordinary loud speech.

`GlobalAudioEffects.SetMasterLimiterThreshold` (RVA `0x433230`) is the only writer and it has
**zero compiled call sites** — not from `Awake`, not from `ResetAll`. The threshold is therefore
the asset default **−3 dB** for the whole session unless a scene has it wired as a
`UnityEvent<float>` target (which static analysis of `GameAssembly.dll` cannot see). Treat −3 dB
as the value, and have `MixerFloats` log it if it is ever written.

After it comes the Dissonance Echo Cancellation plugin effect (the only plugin effect in the
build, and only on `Master`).

### 5.16 Bus levels and the voice slider

`GlobalAudioEffects.Update`, first block:

```
SelfVoiceBus.BusVolume.Volume = VoiceAudioSettingsVol * BusVolume.<+0x10>
v = 20*log10(max(VoiceNormalVol * VoiceAudioSettingsVol, 1e-4))
Mixer.SetFloat("Voice_Dry", v);  Mixer.SetFloat("Voice_Wet", v)        # identical
Mixer.SetFloat("Voice_SuperWet", 20*log10(max(VoiceSuperWetVol * VoiceAudioSettingsVol, 1e-4)))
```

`VoiceNormalVol` (`+0xC4`, ctor 1.0) is only written by `GlobalAudioEffects` itself;
`VoiceAudioSettingsVol` (`+0xC0`) is the settings slider; `VoiceSuperWetVol` is `+0xC8`. Because
`Voice_Dry == Voice_Wet`, the slider is a common factor of the whole voice path and does not
change the dry/wet balance — but `Voice_SuperWet` is independent, so the speechlessness /
blindfold / ending / tower bloom has its own level.

`SetSFXBusVol(v)`, `SetMusic3DVol(v)`, `SetSuperWetEnding(v)`, `SetSuperWetBlackTower(v)` all
apply the same `20·log10(max(v, 1e−4))` conversion `[1e-4: 0x1839764B8, 20: 0x183976DEC]`.

---

## 6. What is *not* an effect on a remote voice

Worth recording so nobody re-derives them:

- **`SelfReverb` / `Master Faint Wet` / the `WET_*` parameter set** — only the `Self Voice` group
  feeds it, and only `LocalVoicePlayer` and the `pitch detector mixer` feed that. No remote voice
  ever passes through it.
- **`Master Super Wet` in normal play** — its four children sit at −80 dB, so the path is silent
  unless speechlessness, blindfold, ending or black tower opens one.
- **Unity `AudioReverbZone`** — none exists in the build; the single `AudioReverbFilter` is
  disabled and on a stage-speaker prop. "Reverb zones" here are the game's own `ReverbZone`
  triggers, which only override the four `AudioDynamicReverb` scalars.
- **`SecretZoneController`, `SleeplessZone`, `SpecialCorpseZone`, `DisconnectionZone`,
  `TeachingZone`, `MedalZone`** — no audio members at all.
- **`MegaphoneSecretZone`** — no such string exists in `global-metadata.dat`. The only
  megaphone-related literals are `Megaphone{1..4}{Dry,Wet}` and `MegaphonePitch`; the megaphone's
  Dissonance room name lives in `RadioVoiceAssigner.roomName` *(prefab data;* M3 run 4 observed
  `MegaphoneA` at runtime*)*.
- **`FoleyPitch`** — exposed and reset, never lowered by any code path.
- **`PeckEffectPipeVideoAudio`** — writes `BigScreenDryLevel` on its own serialized
  `bigScreenMixer`, not the main mixer.
- **`LoadingMenu.OnEnable`** — `SetSFXBusVol(0)` and `Music3DVol = −80`: SFX and music buses only,
  never a `Voice_*` bus.
- **`AmbiencePlayer.LateUpdate`** — `OceanAmbHP`, `AmbExtHP`, `AmbExtLP`, `BiomeAmbLP`: ambience
  only. Its `_mixer` (`+0x1B0`) is assigned in `Start` from `AudioDynamicReverb.Mixer`, i.e. the
  main mixer, but every parameter it touches is on an ambience group.
- **`BlindfoldPopper`, `GameStartBlind`, `EndingFadeBlind`** — no audio members at all (UGUI image
  fades and a server-side launcher).

---

## 7. Coverage map: the mod's Core stages against this catalogue

| Core stage | Reproduces | Does not reproduce |
|---|---|---|
| `VoiceDynamics` + `VoiceCompressor` + `VoiceMakeupGain` | scenario 1 exactly | — |
| `PeakingEq` | scenario 2 (kernel exact; wet mix is a config knob because the Self-Ear curves are degenerate) | — |
| `MixerStageModel` + `MixerStage` | scenarios 3, 5, 6 | scenario 4's filter shape (3 kHz shelf instead of a 250 Hz band split); scenario 6's `adr.ReverbTime` input; the `Reverb Fall` return's real 4 s / `DecayHFRatio 2` character; the +3 dB return gain |
| `MegaphoneVoice` | scenario 12's in-process half (BitCrusher, 300 Hz HP) and the two dynamics stages | the megaphone mixer's fixed LP 5 kHz and 2.5 kHz ParamEQ; its 100 ms Echo; its live SFX reverb (at `d = 0`: −10 dB wet, 2 s); the correct compressor timings (10 ms / 1000 ms, not 50 / 50) |
| `EnvironmentReverb` + `SfxReverb` | scenario 7, and scenario 11 by way of `MasterWet` | the master limiter and the master EQ that follow it |
| `TransmitGate` / `TransmitFader` | scenario 23's transmit half | — |
| *(no stage)* | — | scenario **4b** (voice-blocking materials, effect unknown), **7b** (indoor −6 dB), 8-10 (red bells), 13-14 (walkie), 15 (cliff echo), 16-18 (blindfold, headphone), 19-20 (ending, tower), 24 (gibberish), 25-26 (master limiter and EQ). Scenarios 20b, 21-23 and 23b need nothing from the mod. |

**Ranked gaps.**

0. **Indoor voice attenuation (row 7b).** One multiply from a value the mod already reads, and it
   is a flat −6 dB indoors on everything. Cheapest fix in this list and the most likely cause of
   the "Local Voice is a little hot" report; it also explains why the hallway A/B compared badly
   against what peers hear.
1. **Red bells / speechlessness (rows 8-10).** Five terms, all readable, none implemented. Row 8
   alone (`_speechlessVol`) is a plain linear gain and is a few lines. Rows 9-10 need a pitch
   shifter and the fixed super-wet reverb + chorus, which is real DSP work. The operator hit this
   directly in M3 run 4.
2. **Cliff echo (row 15).** Two extra voice copies with 0.7-1.5 s delays, an LP/HP pair, a ducker
   and a 6 s reverb. Outdoors this is loud and obvious; it is already the M4 seed.
3. **Master limiter (row 25).** The whole mix, including the environment reverb, hits a −3 dB
   10:1 brick wall. This is the most likely explanation for level differences between the Sink and
   what a peer records, and it is a ~40-line `Compressor` reuse.
4. **Megaphone mixer completion (row 12).** Four missing fixed effects plus two wrong time
   constants — cheap, and the operator has a megaphone check outstanding anyway.
5. **Blindfold on the wearer's voice (row 16).** A full-wet `LowPass 1500 Hz Q 0.6` on the local
   player's voice as everyone else hears it, whenever the local player wears a blindfold. This is
   a *speaker-side* effect, so it belongs in Local Voice exactly like the megaphone does, and the
   mod already has the `Biquad` kernel — the only new work is detecting the state (see the
   `PeckEffectMask` branch in 5.6; there is no mixer float to watch, so it has to come from the
   held/worn prop or from `PlayerVoicePlaybackControl._blindFoldMode` on some *other* player's
   control, which the local machine does not have for itself). Rows 17-18 (blindfold/headphone
   *listening* tone) are simple master-path filter changes and only matter while the state is
   active.
6. **`High{n}` filter shape (row 4).** Currently a 3 kHz shelf, should be a 250 Hz band split.
   Zero effect at the Self-Ear; fix it when occlusion is ever modelled.

---

## 8. Errata found in the existing reference docs

1. **`big-walk-voice-dsp.md` section 4** — the second factor of `boost` is
   `AudioDynamicReverb.ReverbTime` (`adr +0x38`), not a "global voice volume". Verified: the
   getters are `RoomSize +0x30`, `Outdoorness +0x34`, `ReverbTime +0x38`, `Diffusion +0x3C`
   (bodies `mov xmm0,[rcx+0x30/34/38/3C]`), and `PlayerVoicePlaybackControl.Update` reads
   `[rbx+56]`. This propagated into `TravelEar.Core.MixerStageInputs.GlobalVoiceVolume`.
2. **`big-walk-voice-dsp.md` section 4** — `playerCharacter.<0x168>.<0xC8>` is
   `playerCharacter.lips.audibility`: `lips` is `PlayerCharacter +0xA8` (`+0x168` is
   `PlayerVegetation`, which has no `+0xC8`), and `audibility` is `PlayerLips +0xC8`.
3. **`big-walk-voice-dsp.md` section 4** — `playerCharacter.<0x110>.<0x44>` is
   `playerCharacter.faller.isInDanger` (`PlayerFaller +0x44`), and `<0x1A0>.<0x14C>` is
   `playerNetworking.outdoorness`. (The mod already uses the right fields.)
4. **`big-walk-local-voice-wiring.md` section 3** — the megaphone master sends are **swapped**.
   The statics are `MEGAPHONE_WET @ +0x00` and `MEGAPHONE_DRY @ +0x08`; the remote-holder branch
   writes `+0x08` (`Dry`) `= t600·−12` and `+0x00` (`Wet`) `= 20·log10(max(t500, 1e−4))`. So at
   `d = 0` the megaphone is **fully dry** (`Dry 0 dB`, `Wet −80 dB`), not fully wet. The local
   branch (`Dry 0`, `Wet −80`) was correct and is what pinned the array order.
5. **`big-walk-environment-reverb.md` section 0** — the `Reverb Boost` return's *asset defaults*
   are the same as the fall reverb's (`Room 0, RoomHF −3000, DecayTime 4, DecayHFRatio 2,
   Reflections −2000, ReflectDelay 0.3, Reverb −1500, ReverbDelay 0.1, Diffusion 100, Density 100,
   HFReference 5000, RoomLF −4000, LFReference 250`), not Unity's off preset. They only matter for
   the first frame before `GlobalAudioEffects.Update` overwrites 13 of them.
6. **`big-walk-environment-reverb.md` section 6** — the open question "the asset's actual default
   [of `MasterWet`] has not been read from the mixer YAML yet" is closed: **0 dB**.
7. **`big-walk-environment-reverb.md` section 0** — the two master ParamEQs have `octave = 0.7`
   (the doc gave only the centre frequencies), and `Master` sends into its own Duck Volume, which
   makes it a self-sidechained limiter rather than a ducker.
8. **`big-walk-environment-reverb.md` section 2** — the claim "No snapshot transitions exist
   anywhere" is now proven rather than asserted: one snapshot per mixer in the assets, and zero
   callers of the snapshot API (section 4).
9. **`big-walk-voice-dsp.md` section 0** — `Filters[2]` (`_blindFoldFilter`) is described as
   "`[Bypass unless blindfolded]`", and section 4 has `PlayVoice` unconditionally appending it.
   Neither is the mechanism: `ApplyBlindFoldFilter` (RVA `0x3B0E70`) returns immediately unless
   `_blindFoldMode` is already set, `SetBlindFoldMode(false)` **destroys** the component and
   removes it from `_filters`, and no `Bypass` field is ever written. When nobody is blindfolded
   there is no third filter at all. Also, the filter runs **fully wet** (`_vol = 1`,
   `_dryWet = 1` from the `BiquadFilters` constructor; `ApplyBlindFoldFilter` overrides only
   `Type`, `_frequency` and `_q`).
10. **`big-walk-voice-dsp.md` section 0** marks the driver of `IAudioFilter.UpdateVariables(deltaTime)`
    as an inference ("*inference*: AudioManager's per-frame controller sweep"). It is now resolved:
    `AudioManager.LateUpdate` (RVA `0x517CA0`) walks its `_lateUpdateSet` and calls
    `AudioSourceController.DoUpdate` (RVA `0x52E920`), which calls `UpdateVariables(dt)` on each
    entry of `_filters` (slot 0 first). The full `LateUpdate` order is: complete the basic
    occlusion job → per-`AudioOcclusionBasic` process and lerp (10/s) → complete the complex
    occlusion job → the `_lateUpdateSet` sweep (`DoUpdate` → `UpdateVariables`) →
    `AudioDynamicReverb.UpdateReverb` and `UpdateEcho` → `AudioBasicReverb.UpdateReverb` → a camera
    floor ray → `AudioReferenceManager.AudioLateUpdate` → `AudioClock` alarms.
11. **`big-walk-voice-dsp.md` section 4** — the `Angle` RTPC axis is
    `clamp01((dot(speakerForward, dirToListener) − 1)·−0.5)·180`, a **linear** map of the speaker's
    facing, not an `acos` of the listener's bearing. It is a speaker-side term.
12. **`big-walk-environment-reverb.md` section 2** lists `GlobalAudioEffects.{ResetSpeechlessness,
    SetSuperWetEnding, SetMasterLimiterThreshold}` among the `SetFloat` callers. They are, but all
    three have **zero compiled call sites**: `ResetSpeechlessness`'s body is inlined into `Update`
    and `ResetAll`, `SuperWet_Ending` is written inline by `EndingTransition.AudioTransitionUpdate`,
    and nothing at all moves `MasterLimiterThreshold`.

---

## 9. Quick reference: what the mod can read at runtime for the uncovered scenarios

All of these are public and require no new hooks beyond `GameSymbols.Bind` entries.

```
// red bells
var me = WorldManager.localPlayerCharacter;
float sp = me.speechless.speechlessness;                    // 0..1, both speaker and listener term
var zone = me.speechless.speechlessZone;                    // null outside; .outerRadius / .innerRadius
var gae  = GlobalAudioEffects.Instance;
float pitchDeduction = gae.SpeechlessPitchDeduction;        // public field +0x80
float musicPitchDed  = gae.MusicPitchDeduction;             // public field +0x84
// -> speechlessVol  = 1 - sp                (linear, lerp dt*5)
// -> voicePitch     = 1 - sp * pitchDeduction
// -> superWetDb     = (1 - sp^0.4) * -80
// -> superWetPitch  = 1 - sp^0.4 * musicPitchDed
// -> masterWetDb    = sp^10 * -80           (already tracked via MixerFloats)

// cliff echo
float echoAmount = me.playerNetworking.echoAmount;          // +0x148, synced every 2 s
float outdoor    = me.playerNetworking.outdoorness;         // +0x14C
var se = SelfEcho.Instance;                                 // .EchoAmount[6] {Amount, Delay, Decay}
bool  echoOn = se.EchoOn;
float listenerOutdoor = AudioManager.Instance.AudioDynamicReverb.Outdoorness;

// blindfold worn by the LOCAL player  -> peers apply LowPass 1500 Hz Q 0.6 to our voice
// The same PeckEffectMask branch that calls SetBlindfold also sets this, on our machine only:
float bf = WorldManager.Instance.postProcessingManager   /* +0x58 */
                       .blindfoldPPVolume                /* +0x38 */
                       .weight;                          /* +0x2C */   // 1 while blindfolded, else 0
// (alternative: find the PeckEffectMask whose maskType == Blindfold and whose peck is ours;
//  PeckEffectMask.maskType is +0x48, enum { Binoculars=0, Telescope=1, Blindfold=2 })

// blindfold / headphone / limiter: all arrive through MixerFloats as SetFloat writes
//   "MasterLP", "MasterFreqGain3k", "MasterFreqGain1k", "MasterLimiterThreshold",
//   "SuperWet_Blindfold", "SuperWet_Speechlessness", "SuperWet_Ending", "SuperWet_BlackTower",
//   "SuperWetPitch", "VoicePitch", "Voice_Dry", "Voice_Wet", "Voice_SuperWet"
```

`MixerFloats` (the Harmony postfix on `AudioMixer.SetFloat`) already sees **every** parameter in
section 3, because they are all written by `SetFloat` and nothing else. That is the cheapest way
to drive all of section 5 without adding a symbol per field. The values it cannot supply are:

- parameters the game never writes, which keep their asset default (section 3) —
  notably `MasterLimiterThreshold` (−3 dB) and every per-channel default;
- the two prefab floats `SpeechlessPitchDeduction` (`GlobalAudioEffects +0x80`) and
  `MusicPitchDeduction` (`+0x84`), which are plain public fields;
- `_outdoornessVol` and `_speechlessVol`, which are `AudioVolume`s in the source's gain chain, not
  mixer floats — but both are trivially recomputed from `adr.Outdoorness` and
  `me.speechless.speechlessness`;
- the per-source filter *existence* changes (the blindfold `BiquadFilters`), which are
  `AddComponent` / `Destroy`, not parameter writes.

**Name collisions in `MixerFloats`.** The Harmony postfix sits on `AudioMixer.SetFloat` for *every*
mixer, and the game reuses parameter names across mixers, so `TryGet(name)` returns whichever mixer
wrote last:

| name(s) | written on |
|---|---|
| `Room RoomHF RoomLF DecayTime DecayHFRatio Reflections ReflectDelay Reverb ReverbDelay HFReference LFReference Diffusion Density` | **main mixer** (`Master Wet`, from `AudioDynamicReverb.UpdateReverb`) **and** the **voice mixer** (`Reverb Boost`, from `GlobalAudioEffects.Update`). Both carry the same values one frame apart, so the collision is benign — but `DryLevel` is main-mixer only, so the 14th value never aliases. |
| `CompressorThreshold CompressorGain PostCompressorThreshold PostCompressorRelease PostCompressorGain ReverbDry ReverbWet ReverbDecayTime ReverbDecayHFRatio ReverbDensity ReverbLF HPFrequency LPFrequency` | all four **megaphone mixers**; four megaphones in a scene write the same 13 names |
| `WalkieTalkieLP` | the **radio mixer** and **walkietalkie mixer 1..10** |
| `DryLvl WetLvl High` | the four **train horn mixers** (`High` here is *not* `High1..12`) |
| `MasterVol` | the **echo mixer** only |

Every name the mod actually needs for section 5 — `MasterWet`, `SuperWet_Speechlessness`,
`SuperWetPitch`, `VoicePitch`, `SuperWet_Blindfold`, `SuperWet_Ending`, `SuperWet_BlackTower`,
`MasterLP`, `MasterFreqGain1k/3k`, `MasterLimiterThreshold`, `Voice_Dry/Wet/SuperWet`, and the
per-channel `Dry{n}`/`High{n}`/`ReverbFallWet{n}`/`ReverbBoostWet{n}` — is unique across mixers.

One caveat if the mod ever writes a mixer float rather than reading one: **`GlobalAudioEffects.Update`
rewrites 13 reverb parameters on the *voice* mixer (`Room`, `RoomHF`, `RoomLF`, `DecayTime`,
`DecayHFRatio`, `Reflections`, `ReflectDelay`, `Reverb`, `ReverbDelay`, `HFReference`,
`LFReference`, `Diffusion`, `Density`, i.e. the `Reverb Boost` return) plus `VoicePitch` every
single frame**, and `AudioDynamicReverb.UpdateReverb` rewrites the 14 on the main mixer every
`LateUpdate`. Anything written there is clobbered on the next frame.

---

## 10. Open questions / unresolved

1. **`SpeechlessPitchDeduction` and `MusicPitchDeduction` values are unknown.** They are serialized
   on the `GlobalAudioEffects` prefab; the binary only shows the fields (`+0x80`, `+0x84`). Read
   them at runtime and log them once — until then no numeric prediction of `VoicePitch` or
   `SuperWetPitch` is possible, only the shape.
2. **`SpeechlessZone.outerRadius` / `innerRadius` are scene data.** Same story: readable at
   runtime through `me.speechless.speechlessZone`. The bells' actual radii are unknown here.
3. **What triggers `is2DVoice`.** `CmdSet2DVoice` and `PlayerLips.Set2DVoice` have no in-code
   callers in the recovered call graph (only the SyncVar hook `OnSet2DVoice`). Either it is set
   from a moderation/debug path the call analyser could not resolve, or it is dead. `isGhost` is
   the live path into the same `TwoDMode` state.
4. **The `RadioVoiceAssigner.roomName` of the megaphone prefab.** Prefab data. M3 run 4 saw
   `MegaphoneA` as a transmitting token room, which is consistent with the megaphone's assigner,
   but the mapping from room name to `_megaphoneIndex` (which selects `megaphone mixer 1..4` via
   `Regex.Match(Cue.name, "\d+") - 1`) is not derivable from the binary.
5. **Unity's built-in effect type ids** are still *(inferred)* from parameter counts and where the
   exposed names land: `−2` Attenuation, `−3` Send, `−4` Receive, `−5` Duck Volume, `6` Echo,
   `10` ParamEQ, `11` Pitch Shifter, `12` Chorus, `16` Compressor, `17` SFX Reverb,
   `18` Lowpass Simple, `22` Highpass Simple, `1000` plugin. The SFX Reverb identification is
   unambiguous (14 parameters in Unity's documented order carrying `DryLevel..Density`); the rest
   are labels. `Echo`'s parameter order (`Delay, DecayRatio, MaxChannels, DryMix, WetMix`),
   `Chorus`'s and `Duck Volume`'s are Unity's documented orders applied to the stored index lists,
   not read from the engine.
6. **Units of the mixer effect parameters.** The stored values strongly suggest that Unity's
   `Compressor` takes **milliseconds** (`Attack 10`, `Release 1000` on the megaphone mixer;
   `15 / 1000` on the radio mixer; `50 / 1000` on the self voice mixer) while `Duck Volume` takes
   **seconds** (`Attack 0`, `Release 0.125` on the main mixer's limiter, and the game writes
   `PostCompressorRelease = 2·att + 0.25` for the megaphone). Those are the values in the asset;
   the units were not confirmed against the engine. `TravelEar.Core.MegaphoneVoice` currently uses
   `UnityCompressorAttackMs = 50` / `UnityCompressorReleaseMs = 50` for the megaphone's
   `Compressor`, which matches neither reading — 10 ms / 1000 ms is what the asset holds.
   Likewise `Duck Volume`'s `Ratio` (10 on the master limiter, 5 on the megaphone post-compressor,
   4 on the echo emitters) is stored as a bare number; whether Unity reads it as `N:1` or as a
   percentage was not verified.
7. **`Echo` with `DryMix 0, WetMix 1`** appears on the megaphone mixer and on every `echo mixer`
   emitter. Whether FMOD's echo unit still passes the first (undelayed) tap in that configuration
   was not determined; it decides whether the megaphone's 100 ms echo is a slapback *added to* the
   voice or a 100 ms *delay of* the voice.
8. **The RTPC curves that consume `VoiceBlock`.** `VoiceBlockingLvl` is computed and exposed, but
   what it *does* to a voice lives entirely in the voice Cue's `AudioRTPC` curve set, which is
   asset data. Reading it needs the Cue assets (the same UnityPy route used for the mixers, one
   level further in) or a live read off
   `PlayerVoicePlaybackControl.controls[i].SourceController` with a remote player present. Until
   then, "voice through a wall gets quieter/duller by an unknown amount" is all this note can say.
   The related `PlayerVoicePlaybackControl.voiceBlockingMask` field (`+0x30`) is dead code.
9. **`StandaloneOcclusion.Update` (RVA `0x445D80`)** writes
   `<serialized FilterParam> = Occlusion.OccLvl · MinGain` on a **serialized** `AudioMixer` field.
   Neither the mixer reference nor the parameter name is a literal in the binary, so whether this
   can ever land on a voice-carrying mixer is **undetermined**. If `MixerFloats` ever logs a
   parameter not in section 3, this is the likely source.
10. **The exposed name behind CRC `2545798526`** (on `Music 3D Fixed Speakers`) was recovered as
   `MusicVol` by brute force, not by finding the literal in `global-metadata.dat`. Nothing writes
   it, so the collision risk is immaterial.
11. **The `AudioSourceController` flag written alongside `TwoDMode`** is `_bypassFilters` (`+0x129`,
   property `BypassFilters`, RVA `0x52E440` / `0x52E470`). Its getter is
   `ScriptableVolume == 0 || _finalVolume == 0 ? true : _bypassFilters` — i.e. it also short-circuits
   for a silent source. **Nothing reads it**: `get_BypassFilters` has no call site in either
   `Assembly-CSharp` or `AudioSystem` (`CallerCount = 0`, and no reference to its RVA in the ISIL
   dumps), so setting it for a ghost or a 2D voice is currently inert. If a future build wires it
   up, a ghost's voice would lose the whole in-process filter chain — including the
   `SamplePlaybackComponent` that produces the audio — so this is worth re-checking after a game
   update.

# M2 / T1 "bodies read" — the remote-voice listener chain in Big Walk

> **Errata (2026-09-09):** `big-walk-voice-effects-catalog.md` section 8 lists corrections to this document found by the full effects catalogue (e.g. the boost term's second factor, the megaphone master send order, filter drivers). Read that section before relying on a formula here.

Sources: ISIL x64 lift (`IsilDump`), diffable-cs (offsets/RVAs), `dll_il_recovery` +
`callanalyzer` attributes, and float constants read directly out of
`Big Walk\GameAssembly.dll` (image base `0x180000000`, PE section walk).
Every numeric constant below was resolved from the binary unless marked *(inferred)*.

---

## 0. Chain order per audio frame

The remote voice is a pooled `AudioSourceController` playing a **constant-1.0 AudioClip**
(`PlayerVoicePlaybackControl.RebuildCachedClip` → `<>c.<RebuildCachedClip>b__63_0` writes
`0x3F800000` into every sample of the PCM callback buffer). So the buffer that reaches
`OnAudioFilterRead` already carries Unity's spatial/attenuation/mixer gain as a per-sample
envelope. `PlayVoice` does **not** enable `_filterSynthesizerMode` — it only copies the
controller's existing flag into the mixer (`_sourceController._filterMixer.SynthesizerMode =
_sourceController._filterSynthesizerMode`, offsets `0x110`/`0x128`).

```
Unity audio thread, once per DSP block:
  AudioFilterMixer.OnAudioFilterRead(data, channels)          (AudioSystem)
    -> Filters[0] = SamplePlaybackComponent.ProcessSamples(ref data, channels)
         reads Opus/jitter-buffered PCM from SpeechSession into _temp
         per sample: gain-ramp -> VoiceCompressor.Process -> SoftClip
                     data[i] *= voice        (multiplies into the 1.0-envelope)
                     CachedVoiceData[wh++] = voice   (pre-multiply tap ring)
    -> Filters[1] = BiquadFilters (_eqFilter) .ProcessSamples(ref data, channels)
         PeakingEQ 400 Hz Q 0.3 gain +30 dB, Vol 0.03, DryWet driven from Update()
    -> Filters[2] = BiquadFilters (_blindFoldFilter) .ProcessSamples(...)  [Bypass unless blindfolded]
    -> clamp(data[i], -1, 1)                                   (non-synth path)

Unity main thread, once per Update:
  PlayerVoicePlaybackControl.Update()
    -> TrySetUp / UpdateSourceHealth
    -> writes _eqFilter._dryWet, _outdoornessVol/_amplitudeVol/_speechlessVol
    -> 4 AudioMixer.SetFloat: Dry{n}, High{n}, ReverbFallWet{n}, ReverbBoostWet{n}
    -> VoiceMakeupGain.Evaluate(playerName, spc._arv, isSpeaking, dt)
         -> SamplePlaybackComponent.MakeupGain    (feeds the *next* audio block)
  IAudioFilter.UpdateVariables(deltaTime) is the per-frame main-thread hook of each filter;
  BiquadFilters recomputes its coefficients there. SamplePlaybackComponent.UpdateVariables is
  an empty method (`ret`). (The exact driver of UpdateVariables is interface-dispatched, so
  callanalyzer records no caller — *inference*: AudioManager's per-frame controller sweep.)
```

Registration order comes from `PlayVoice` (ISIL 180..349):
`AddVolume(_outdoornessVol)`, `AddVolume(_speechlessVol)`, `AddFilter(dissonanceSampleProvider, 0)`,
`spc.AmplitudeOnlyMode = false`, `AddComponent<BiquadFilters>()` appended to
`_sourceController._filters`, then `ApplyBlindFoldFilter()` appends a third.
`_amplitudeVol` is **only** added in `PlayGibberish` (which instead sets
`spc.AmplitudeOnlyMode = true`), even though `Update` writes its `Volume` unconditionally.

---

## 1. `Dissonance.Audio.Playback.SamplePlaybackComponent` (DissonanceVoip)

`MonoBehaviour, IAudioFilter, IVoiceDataProvider`

### Fields
| off | field |
|---|---|
|0x20|`float[] _temp`|
|0x28|`AudioFileWriter _diagnosticOutput`|
|0x30|`SessionContext _lastPlayedSessionContext`|
|0x40|`ReaderWriterLockSlim _sessionLock`|
|0x48|`Nullable<SpeechSession> <Session>k__BackingField`|
|0x88|`float _arv`|
|0x8C|`float _preClipPeak`|
|0x90|`float _outputArv`|
|0x94|`float _compressorReduction`|
|0x98|`int _pullCount`|
|**0x9C**|**`public float MakeupGain`**|
|0xA0|`float _currentGain`|
|0xA4|`VoiceCompressor _compressor` (inline struct, 0x20 bytes)|
|0xC8|`float[] <CachedVoiceData>`|
|0xD0|`int <CachedVoiceWriteHead>`|
|0xD8|`Action OnWriteHeadJump`|
|0xE0 / 0xE1|`_internalBypass` / `_bypass`|
|0xE2|`bool <AmplitudeOnlyMode>`|

Consts: `SoftClipKnee = 0.85`, `SoftClipCeiling = 0.95`, `SoftClipHeadroom = 0.099999964`.

### `SoftClip(float sample, float magnitude)` — RVA 0x6F3750
```
if (magnitude <= 0.85f) return sample;                 // untouched below the knee
over = magnitude - 0.85f;
y    = 0.85f + (over * 0.09999996f) / (over + 0.09999996f);
return sample < 0 ? -y : y;                            // sign restored via 0x80000000 xor
```
Hyperbolic knee, asymptote 0.95. It is always called as `SoftClip(x, |x|)`.

### `ProcessSamples(ref float[] data, int channels)` — RVA 0x6F2DA0 (the real per-block entry)
1. `if (Session == null)` -> `Array.Clear(data)` **and** `Array.Clear(CachedVoiceData)`, return.
   (The remote path actively silences the mixer buffer when nobody is speaking.)
2. `_sessionLock.TryEnterUpgradeableReadLock()`.
3. If the `SessionContext` changed (memcmp of the id blob plus a second field) -> reset:
   `_arv = _preClipPeak = _outputArv = 0`, `_compressorReduction = 1`,
   `_currentGain = MakeupGain`, `_compressor.Reset()` (peak/envelope = 0, `Reduction = 1`).
4. `_compressor.Prepare(AudioFilterBase.OutputSampleRate)` — every block.
5. `count = data.Length / channels`; `session.Read(new ArraySegment<float>(_temp, 0, count))`
   (interface-dispatched `IDecoderPipeline.Read`); returns "session complete".
   If `_diagnosticOutput != null` -> `WriteSamples(segment)`.
6. **AmplitudeOnlyMode == true** (gibberish): measure only —
   `arvSum += |s|`, `preClipPeak = max(preClipPeak, |s| * MakeupGain)`,
   `CachedVoiceData[(wh + i + c) & mask] = data[i+c]` (copies the *output*, not the voice);
   `data` is left untouched. `outputArv = arvSum * MakeupGain / count`.
7. **AmplitudeOnlyMode == false** (normal voice), per sample `s = _temp[k]`:
   ```
   step = (MakeupGain - _currentGain) / count;   g = _currentGain     // per-sample ramp
   arvSum += |s|
   y = _compressor.Process(s * g)
   reduction = min(reduction, _compressor.Reduction)                  // block minimum
   preClipPeak = max(preClipPeak, |y|)
   y = SoftClip(y, |y|)
   for c in 0..channels-1:
        data[i+c] *= y                                                // MULTIPLY, not assign
        CachedVoiceData[(wh + i + c) & (len-1)] = y                    // pre-multiply tap
   outSum += |y|;   g += step
   ```
8. Epilogue: `_pullCount++`, `_currentGain = MakeupGain`,
   `CachedVoiceWriteHead = (CachedVoiceWriteHead + data.Length) & (len-1)`,
   `_arv = arvSum / count`, `_preClipPeak`, `_compressorReduction = reduction`,
   `_outputArv = min(outSum / count, 1.0f)`.
9. If the session reported complete -> write lock, `Session = null`, reset the stats, dispose the
   diagnostic writer.

### `Filter(...)` static — RVA 0x6F3790
Byte-for-byte the same algorithm as steps 5-8, parameterised
(`targetGain`, `ref currentGain`, `ref compressor`, `out arv/outputArv/preClipPeak/reduction`).
**`CallerCount = 0`** — dead in this build; the game inlined it into `ProcessSamples`. It is
still the cleanest reference for a port.

### Other members
- `RecommendedVoiceReadHead => (CachedVoiceWriteHead + 1) & (CachedVoiceData.Length - 1)`
  (only **+1**, unlike `LocalVoiceProvider`'s `-2 * bufferLength * numBuffers * channels`).
- `InitializeRawBuffer`: `CachedVoiceData = new float[nextPow2(bufferLength * numBuffers *
  AudioUtil.GetUnityChannels(speakerMode))]` — same sizing as `LocalVoiceProvider`.
- `UpdateVariables(float)` — empty (`ret`).
- `Play(SpeechSession)` — `CalledBy VoicePlayback.Update`; sets `Session`, optional WAV diagnostics.

### Call graph
`VoicePlayback.Update -> Play`; `AudioFilterMixer.OnAudioFilterRead -> ProcessSamples` (vtable);
`ProcessSamples -> VoiceCompressor.Prepare/Process, SoftClip, SpeechSession.Read`;
`PlayerVoicePlaybackControl.Update` reads `_arv`, `_outputArv`, `Session` and writes `MakeupGain`.

---

## 2. `VoiceCompressor` (AudioSystem) — a 0x20-byte struct, not a component

### Fields / consts
`_peak@0x0, _envelope@0x4, _threshold@0x8, _kneeWidth@0xC, _sampleRate@0x10,
_attackCoefficient@0x14, _releaseCoefficient@0x18, Reduction@0x1C`.
`static float Threshold` (`.cctor` -> `0.6f`), `MAX_THRESHOLD 0.6`, `KNEE_FACTOR 1.4`,
`RATIO 2`, `ATTACK_SECONDS 0.005`, `RELEASE_SECONDS 0.15`, `EXPONENT 0.5`,
`DENORMAL_FLOOR 1e-12`, `DebugBypass false`.

### `Reset()`
`_peak = _envelope = 0; Reduction = 1`.

### `Prepare(int sampleRate)` — RVA 0x544C80
```
_threshold = VoiceCompressor.Threshold;      // static, re-read every call
_kneeWidth = _threshold * 0.4f;              // 0.4 == KNEE_FACTOR-1 (const at 0x183976728)
if (sampleRate > 0 && sampleRate != _sampleRate) {
    _sampleRate         = sampleRate;
    _attackCoefficient  = 1f - expf(-1f / (sampleRate * 0.005f));
    _releaseCoefficient = 1f - expf(-1f / (sampleRate * 0.15f));
}
```

### `Process(float sample)` — RVA 0x544D50
```
m = |sample|;
if (m > _peak) _peak = m;                                        // instant attack
else { _peak += (m - _peak) * _releaseCoefficient;                // exponential release
       if (_peak < 1e-12f) _peak = 0; }
_envelope += (_peak - _envelope) * _attackCoefficient;            // smoothing
if (_envelope < 1e-12f) _envelope = 0;

if (_envelope <= _threshold) gain = 1f;
else {
    t    = min((_envelope - _threshold) / _kneeWidth, 1f);        // soft-knee blend, 0..1
    gain = powf(_threshold / _envelope, t * 0.5f);                // 0.5 == 1 - 1/RATIO (2:1)
}
Reduction = gain;
return sample * gain;
```
Fully self-contained scalar DSP; the only external input is the static `Threshold`, which
`VoiceMakeupGain.set_TargetARV` keeps at `min(TargetARV * 4, 0.6)`.

---

## 3. `VoiceMakeupGain` (Assembly-CSharp) — static class, per-player state dictionary

### State
`private class State { float Envelope@0x10; float Level@0x14; float SpeechSeconds@0x18;
float GainDb@0x1C; }` — plain ref type, no Unity dependency.
Statics: `float s_targetARV@0x0` (`.cctor` -> `0.132f`), `Dictionary<string,State> s_states@0x8`.

Consts: `REFERENCE_ARV 0.132`, `COMPRESSOR_CREST 4`, `MIN_TARGET_ARV 0.01`, `MAX_TARGET_ARV 0.4`,
`SLEW_UP_DB_PER_SECOND 12`, `SLEW_DOWN_DB_PER_SECOND 1`, `SETTLE_DB_PER_SECOND 24`,
`SETTLE_SECONDS 1`, `LEVEL_DROP_SECONDS 2`, `LEVEL_CLIMB_SECONDS 6`, `LEVEL_WARMUP_SECONDS 0.25`,
`ENVELOPE_ATTACK_SECONDS 0.15`, `ENVELOPE_RELEASE_SECONDS 1`, `SPEECH_FLOOR 0.005`,
`RELATIVE_GATE 0.1`, `CONFIDENCE_SECONDS 0.5`, `VOLUME_CURVE 1.5`, `DebugBypass false`.

### Properties
```
set_TargetARV(v): s_targetARV = max(v, 0.01f);                    // no upper clamp in the setter
                  VoiceCompressor.Threshold = min(s_targetARV * 4f, 0.6f);
get_VolumeDb   => 20f * log10f(max(s_targetARV / 0.132f, 1e-4f));
get_Volume     => powf(clamp01((s_targetARV - 0.01f) / 0.39f), 1f/1.5f);   // 0.39 = 0.4 - 0.01
set_Volume(v)  : TargetARV = clamp01(powf(clamp01(v), 1.5f)) * 0.39f + 0.01f;  then Clear()
Clear()        : s_states.Clear()
TryGetState(name, out gainDb, out level)   // debug read-out
```

### `Evaluate(string playerName, float arv, bool isSpeaking, float deltaTime)` — RVA 0x44A170
```
if (playerName == null || playerName.Length == 0) return 1f;
st = s_states.GetOrAdd(playerName);

if (deltaTime > 0 && isSpeaking) {
  if (st.Level <= 0) {                        // cold start
      if (arv > 0.005f) { st.Envelope = arv; st.Level = arv; st.SpeechSeconds += deltaTime; }
  } else {
      tauE = (arv > st.Envelope) ? 0.15f : 1f;                        // attack / release
      st.Envelope += (arv - st.Envelope) * clamp01(1f - expf(-deltaTime / tauE));
      gate = max(0.005f, st.Envelope * 0.1f);
      if (arv > gate) {
          tauL = (st.Level > st.Envelope) ? 2f /*(inferred: LEVEL_DROP)*/ : 6f;
          tau  = clamp(st.SpeechSeconds, 0.25f, tauL);                // warm-up ramp
          st.Level += (st.Envelope - st.Level) * clamp01(1f - expf(-deltaTime / tau));
          st.SpeechSeconds += deltaTime;
      }
  }

  targetDb = 0f;
  if (st.Level > 0) {
      targetDb  = 20f * log10f(max(0.132f / st.Level, 1e-4f));
      targetDb *= clamp01(st.SpeechSeconds / 0.5f);                   // confidence fade-in
  }
  rate = (st.SpeechSeconds < 1f) ? 24f
                                 : (targetDb > st.GainDb ? 12f : 1f);
  st.GainDb = Mathf.MoveTowards(st.GainDb, targetDb, rate * deltaTime);
}
return powf(10f, st.GainDb / 20f) * (s_targetARV / 0.132f);
```
Notes: when `!isSpeaking` or `deltaTime <= 0`, nothing is updated and the **last** `GainDb` is
still returned (the gain freezes rather than decaying between bursts).
The `2f` in `tauL` is the one value the ISIL lifter failed to decode
(`Move xmm1, typeof(T2)` at instruction 105); `LEVEL_DROP_SECONDS = 2` is the only matching
const — **inference, high confidence**.

Call graph: `CalledBy PlayerVoicePlaybackControl.Update` only. `VolumeDb` is read by
`AudibilityDebug.DrawGUI`. No Unity types except `Mathf.MoveTowards`.

---

## 4. `PlayerVoicePlaybackControl` (Assembly-CSharp) — `MonoBehaviour, IAudioRTPCXProvider`

### Fields (instance)
`voicePlayback@0x20, dissonanceSampleProvider@0x28, voiceBlockingMask@0x30,
playerCharacter@0x38, FilterDistanceCurve@0x40, FilterAngleCurve@0x48, AttenuationCurve@0x50,
SpatialVolCurve@0x58, GibberishCue@0x60, _cue@0x68, _index@0x70, _mixer@0x78, _cachedClip@0x80,
_sourceController@0x88, _eqFilter@0x90, _attenuation@0x98, _outdoornessVol@0xA0,
_amplitudeVol@0xA8, _speechlessVol@0xB0, _fallWetLvl@0xB8, _smoothedARV@0xBC, _peakARV@0xC0,
<TwoDMode>@0xC4, _setUp@0xC5, _cueUnusable@0xC6, _reportedNoCue@0xC7, _reportedStall@0xC8,
_notPullingTime@0xCC, _restartCooldown@0xD0, _blindFoldFilter@0xD8, _blindFoldMode@0xE0`
Statics: `controls@0x0, cueStack@0x8, _totalCueCount@0x10, AudibilityDebugGUI@0x18,
PARAM_DRY@0x20, PARAM_HIGH@0x28, PARAM_REVERB_FALL_WET@0x30, PARAM_REVERB_BOOST_WET@0x38,
_gibberishMode@0x40`. Consts `STALL_RESTART_DELAY 1`, `STALL_RESTART_COOLDOWN 2`.

### Mixer parameter names (recovered from `global-metadata.dat`, 12 voice channels)
`PARAM_DRY = {"Dry1".."Dry12"}`, `PARAM_HIGH = {"High1".."High12"}`,
`PARAM_REVERB_FALL_WET = {"ReverbFallWet1".."ReverbFallWet12"}`,
`PARAM_REVERB_BOOST_WET = {"ReverbBoostWet1".."ReverbBoostWet12"}`.
`_index` is the 0-based voice channel taken from the cue's mixer-group name (1..12).

### `Update()` — RVA 0x3AED10 (0x9EF bytes; the whole per-frame model)
```
dt = Time.deltaTime;
if (!TrySetUp()) return;
UpdateSourceHealth();

// A. gameplay broadcast (needs remote-player state)
if (WorldManager.Instance && _sourceController && playerCharacter) {
    playerCharacter.<0x168>.<0xC8> = _attenuation * _sourceController._finalVolume;   // audibility
    _speechlessVol.Volume = Mathf.Lerp(_speechlessVol.Volume,
                                       1f - playerCharacter.<0x188>.<0x1C>, dt * 5f);
}

// B. RTPC probes off the pooled source (AudioRTPC.XAxisType)
_sourceController.GetX(ListenerDistance /*0*/,  out dist);
_sourceController.GetX(Angle            /*120*/, out angle);
_sourceController.GetX(OcclusionLevel   /*70*/,  out occl);
spatial = _sourceController._spatialBlend;              // 0xC4

// C. the EQ wet mix
if (_eqFilter) {
    a = FilterAngleCurve.Evaluate(|angle|);
    d = FilterDistanceCurve.Evaluate(dist);
    t = spatial * 0.5f + 0.5f;
    _eqFilter._dryWet = clamp01( t * (1f - (1f-d)*(1f-a)) );   // writes the field, not the property
}

// D. 2D override (-2 is the "no override" sentinel)
if (!TwoDMode) { _sourceController.ScriptableVolume = -2f;  ScriptableSpatialBlend = -2f; }
else           { _sourceController.ScriptableVolume =  1f;  ScriptableSpatialBlend =  0f; }

outdoor = AudioManager.Instance.<0x138> ? AudioManager.Instance.<0x138>.<0x34> : 1f;
_outdoornessVol.Volume += ((outdoor*0.5f + 0.5f) - _outdoornessVol.Volume) * clamp01(dt * 3f);
_amplitudeVol.Volume    = 1f - powf(1f - dissonanceSampleProvider._outputArv, 5f);

// E. attenuation + reverb sends
if (!TwoDMode) {
    _attenuation = min(SpatialVolCurve.Evaluate(spatial), AttenuationCurve.Evaluate(dist));
    wetTarget = (playerCharacter && playerCharacter.<0x110>.<0x44>) ? playerCharacter.<0x1A0>.<0x14C> : 0f;
    _fallWetLvl = (wetTarget > _fallWetLvl) ? wetTarget                      // instant rise
                                            : Mathf.Lerp(_fallWetLvl, wetTarget, dt);
} else { _attenuation = 1f; _fallWetLvl = 0f; occl = 0f; }

boost = (1f - _attenuation)^3
      * globalVoiceVol                        // AudioManager.Instance.<0x138>.<0x38>, else 0
      * clamp01((dist - 45f) / -45f)          // 1 at 0 m, 0 at 45 m
      * (1f - outdoor)
      * (1f - occl)
      * clamp01((srcPos.y - AudioManager.ListenerPosition.y) / 30f);

dryLin = max(_attenuation, boost / 3f * heightFactor, 1e-4f);
_mixer.SetFloat(PARAM_DRY[_index],              max(-80f, 20f*log10f(dryLin)));
_mixer.SetFloat(PARAM_REVERB_FALL_WET[_index],  20f*log10f(max(_fallWetLvl, 1e-4f)));
_mixer.SetFloat(PARAM_REVERB_BOOST_WET[_index], 20f*log10f(max(boost,       1e-4f)));
_mixer.SetFloat(PARAM_HIGH[_index],             occl * -30f);

// F. makeup gain (feeds the next audio block)
if (dissonanceSampleProvider) {
    name       = voicePlayback ? voicePlayback.<0x30>.<0x50> : null;        // player name
    isSpeaking = _sourceController != null && dissonanceSampleProvider.Session.HasValue;
    dissonanceSampleProvider.MakeupGain =
        VoiceMakeupGain.Evaluate(name, dissonanceSampleProvider._arv, isSpeaking, dt);
}

// G. debug-only meters (guarded by AudibilityDebugGUI != null && its enabled flag)
_smoothedARV += (spc._outputArv - _smoothedARV) * clamp01(1f - expf(-dt / 0.015f));
_peakARV = max(_peakARV, _smoothedARV);
_peakARV -= dt * 0.2f * clamp01(_peakARV - _smoothedARV);
```
`clamp01((dist - 45f) / -45f)` is 1 at the listener and 0 at 45 m. `boost / 3f` reuses the same
`3.0` constant as the outdoorness lerp rate.

### `PlayVoice()` — RVA 0x3AFE90
`_fallWetLvl = 0; _attenuation = 1;` -> `SetFloat(Dry{n}, -80)`, `SetFloat(ReverbFallWet{n}, -80)`
-> `AudioManager.Play(...)` -> `_sourceController`
-> `AddVolume(_outdoornessVol, this)`, `AddVolume(_speechlessVol, this)`
-> `AddFilter(dissonanceSampleProvider, 0)`, `spc.AmplitudeOnlyMode = false`
-> `AddComponent<BiquadFilters>()`, appended to `_sourceController._filters`, then
```
_eqFilter.Type       = PeakingEQ (6)
_eqFilter._frequency = 400f      ; _dirty = true
_eqFilter._q         = 0.3f      ; _dirty = true
_eqFilter._gain      = 30f       ; _dirty = true      // +30 dB peak
_eqFilter._vol       = 0.03f
_eqFilter._dryWet    = 0f
```
(+30 dB times Vol 0.03 is ~unity at the 400 Hz peak and far below unity elsewhere: the wet path
is a wide 400 Hz band-emphasis — the "muffled / through-a-wall" voice. `DryWet` crossfades it in.)
-> `ApplyBlindFoldFilter()` -> a second `BiquadFilters`: `Type = LowPass (1)`,
`_frequency = 1500f`, `_q = 0.6f`, stored in `_blindFoldFilter`.
-> `voicePlayback.<0x70> = _sourceController`.

`PlayGibberish()` is the same but sets `spc.AmplitudeOnlyMode = true` and additionally
`AddVolume(_amplitudeVol, this)`.

### Call graph
`Update -> TrySetUp -> {Initialize, TryTakeCue, RebuildCachedClip, PlayVoice | PlayGibberish}`,
`Update -> UpdateSourceHealth -> RestartVoice`, `Update -> VoiceMakeupGain.Evaluate`,
`Update -> AnimationCurve.Evaluate x4`, `Update -> AudioMixer.SetFloat x4`,
`Update -> AudioSourceController.GetX x3`, `Update -> AudioManager.get_ListenerPosition`.

---

## 5. `BiquadFilters` (AudioSystem) — `AudioFilterBase : MonoBehaviour`, `[BurstCompile]`

### Fields
`Type@0x20 (FilterType), _q@0x24 [0.3..5], _frequency@0x28 [100..22000], _gain@0x2C [-30..30],
_vol@0x30 [0..1], _dryWet@0x34 [0..1], _clampLimit@0x38 [1..8], _internalBypass@0x3C,
_bypass@0x3D, _a0@0x40, _b0@0x44, _b1@0x48, _b2@0x4C, _a1@0x50, _a2@0x54, _invA0@0x58,
_w0@0x5C, _alpha@0x60, _A@0x64, _sampleRate@0x68, _cosW0@0x6C, _sqrtAAlpha@0x70, _dirty@0x74,
_lastType@0x78, DelayedSamples[] _delayedSamples@0x80, AppliedCoefficients _applied@0x88,
_appliedInitialized@0xA4`. `static bool DebugBypass`. `DENORMAL_THRESHOLD = 1e-15`.
`enum FilterType { Allpass=0, LowPass=1, HighPass=2, Notch=3, LowShelf=4, HighShelf=5, PeakingEQ=6 }`
`struct DelayedSamples { za1, za2, zb1, zb2 }` (16 B) — one per channel.
`struct AppliedCoefficients { b0, b1, b2, a1, a2, volWet, dryInv }` (28 B).

### `Awake()`
`_sampleRate = AudioSettings.outputSampleRate`; allocates `_delayedSamples` sized by
`AudioSettings.speakerMode` (1/2/4/5/6/8 channels); then the base virtual.

### `UpdateVariables(float deltaTime)` — main thread, RVA 0x53F2D0
```
if (_sampleRate != AudioFilterBase.OutputSampleRate) { _dirty = true; _sampleRate = ...; }
if (_internalBypass || _bypass || DebugBypass) return;
if (!_dirty && _lastType == Type) return;
_dirty = false; _lastType = Type;
_w0    = (_frequency / _sampleRate) * 6.2831855f;
_cosW0 = cosf(_w0);
_alpha = sinf(_w0) / (2f * _q);
if (Type is LowShelf | HighShelf | PeakingEQ) {
    _A = powf(10f, _gain / 40f);   if (_A == 0f) _A = 1f;
    _sqrtAAlpha = sqrtf(_A) * _alpha;
}
CoefficientCalculation(Type);
```
`GainCalculation(type)` is the same `_A` / `_sqrtAAlpha` pair standalone (unused by this path).
`QandFrequencyCalculation()` is the `_w0`/`_cosW0`/`_alpha` block standalone.

### `CoefficientCalculation(FilterType)` — RVA 0x53F510, textbook RBJ cookbook
| Type | b0 | b1 | b2 | a0 | a1 | a2 |
|---|---|---|---|---|---|---|
|Allpass|1-a|-2cos|1+a|1+a|-2cos|1-a|
|LowPass|(1-cos)/2|1-cos|(1-cos)/2|1+a|-2cos|1-a|
|HighPass|(1+cos)/2|-(1+cos)|(1+cos)/2|1+a|-2cos|1-a|
|Notch|1|-2cos|1|1+a|-2cos|1-a|
|LowShelf|A((A+1)-(A-1)cos+2*sqrtA*a)|2A((A-1)-(A+1)cos)|A((A+1)-(A-1)cos-2*sqrtA*a)|(A+1)+(A-1)cos+2*sqrtA*a|-2((A-1)+(A+1)cos)|(A+1)+(A-1)cos-2*sqrtA*a|
|HighShelf|A((A+1)+(A-1)cos+2*sqrtA*a)|-2A((A-1)+(A+1)cos)|A((A+1)+(A-1)cos-2*sqrtA*a)|(A+1)-(A-1)cos+2*sqrtA*a|2((A-1)-(A+1)cos)|(A+1)-(A-1)cos-2*sqrtA*a|
|PeakingEQ|1+a*A|-2cos|1-a*A|1+a/A|-2cos|1-a/A|

(`a` = `_alpha`, `cos` = `_cosW0`, `sqrtA*a` = `_sqrtAAlpha`.)
Then `_invA0 = (a0 == 0f) ? +Inf (0x7F800000) : 1f / a0`.

### `ProcessSamples(ref float[] data, int channels)` — audio thread, RVA 0x53F940
```
if (_internalBypass || _bypass || DebugBypass || channels <= 0 || data == null || data.Length == 0) return;
if (_delayedSamples == null || _delayedSamples.Length != channels)
    _delayedSamples = new DelayedSamples[channels];
target = { b0:_b0*_invA0, b1:_b1*_invA0, b2:_b2*_invA0, a1:_a1*_invA0, a2:_a2*_invA0,
           volWet:_dryWet*_vol, dryInv:1f - _dryWet };            // b0..a1 done as one mulps
if (!_appliedInitialized) { _applied = target; _appliedInitialized = true; }
Process(&data[0], data.Length, channels, &_delayedSamples[0], ref _applied, ref target, _clampLimit);
_applied = target;                                                 // the ramp always ends on target
```

### `Process$BurstManaged(...)` — RVA 0x53FB50 (`Process` is the Burst direct-call stub)
```
frames = length / channels;   step = frames > 0 ? 1f/frames : 0f;
d.* = (target.* - current.*) * step;                              // 7 per-frame deltas
for (i = 0; i < length; i++) {
    ch = i % channels;  s = &state[ch];  x = data[i];
    y = b0*x + b1*s->zb1 + b2*s->zb2 - a1*s->za1 - a2*s->za2;
    if (y < 1e-15f && y > -1e-15f) y = 0f;                        // denormal flush
    o = y*volWet + x*dryInv;
    o = clamp(o, -clampLimit, +clampLimit);
    data[i] = o;
    s->zb2 = s->zb1; s->zb1 = x; s->za2 = s->za1; s->za1 = y;     // za1 holds y, pre-mix
    if (++chCounter == channels) { chCounter = 0;
        b0+=d.b0; b1+=d.b1; b2+=d.b2; a1+=d.a1; a2+=d.a2; volWet+=d.volWet; dryInv+=d.dryInv; }
}
```
So the output is `(1 - dryWet)*x + (dryWet*vol)*biquad(x)`, with every coefficient linearly
ramped across the block — that is what makes the `Update()`-driven `DryWet` sweep click-free.

---

## 6. Decision inputs — reuse vs. port, per effect

| Effect | Shape | Recommendation |
|---|---|---|
| **`VoiceCompressor`** | plain `struct`, no Unity types, no MonoBehaviour, 8 floats of state | **Port to C#** in the mod's Core with unit tests. It is ~25 lines and fully specified above. Reusing the game struct means marshalling an IL2CPP struct by ref for every sample — not worth it. The only external input is the static `VoiceCompressor.Threshold` (read it once per block, or mirror `min(TargetARV*4, 0.6)`). |
| **`SamplePlaybackComponent`** (gain ramp + compressor + SoftClip + ARV/peak metering) | MonoBehaviour, but its DSP is entangled with `SpeechSession` / `ReaderWriterLockSlim` / decoder pull — we cannot feed it our own PCM without owning a `SpeechSession`. | **Port the DSP** (`Filter`'s body: ramp -> `VoiceCompressor.Process` -> `SoftClip` -> `data[i] *= y`) into the mod's Local Voice renderer, applied to the samples we already push through `LocalVoiceProvider`. `SoftClip` and the `arv`/`outputArv`/`preClipPeak` accumulators are trivial and testable. Attaching a real `SamplePlaybackComponent` is **not** viable. |
| **`VoiceMakeupGain`** | static class, `Dictionary<string,State>`, only `Mathf.MoveTowards` from Unity | **Reusable as-is via interop** — `VoiceMakeupGain.Evaluate(key, arv, isSpeaking, Time.deltaTime)` once per frame is a cheap managed call and keeps us bit-identical with the game (including the shared `TargetARV` / voice-volume slider). Risk: it writes into the game's shared `s_states` dictionary keyed by player name — **use a distinct key** (e.g. `"TravelEar:Self"`) so we never corrupt a real remote player's state. Porting is the safer default (~40 lines, exact constants above); reuse buys free coupling to the in-game volume slider. |
| **`BiquadFilters` (the 400 Hz PeakingEQ)** | a genuine self-contained `AudioFilterBase` MonoBehaviour with a Burst DSP kernel | **Reuse — attach it.** `AddComponent<BiquadFilters>()` on our own `AudioSourceController`'s GameObject, append it to `Controller.Filters` after our provider, and set `Type/Frequency/Q/Gain/Vol` exactly as `PlayVoice` does. The game drives `UpdateVariables` and `ProcessSamples` for us and we get the Burst path for free. We only have to compute `DryWet` per frame ourselves. |
| **`PlayerVoicePlaybackControl.Update` model** | MonoBehaviour, hard-wired to remote-player state | **Port selectively** — most of its inputs do not exist for the local player (see below). |

### State the local player does not have (flag list)
1. `playerCharacter` — `Update` writes `playerCharacter.<0x168>.<0xC8>` (an audibility broadcast)
   and reads `<0x188>.<0x1C>` (drives `_speechlessVol`) and `<0x1A0>.<0x14C>` (the reverb-fall
   target). Our renderer has no `PlayerCharacter`; stub these (`_speechlessVol = 1`,
   `_fallWetLvl = 0`) rather than trying to source them.
2. `_sourceController.GetX(ListenerDistance / Angle / OcclusionLevel)` — for a Self-Ear emitter
   pinned ~3 in from the listener these are degenerate: distance ~ 0, angle ~ 0, occlusion 0. That
   makes `FilterDistanceCurve` / `FilterAngleCurve` evaluate at their left edge and
   `_eqFilter.DryWet = clamp01(t * (1 - (1-d)*(1-a)))` collapse to whatever those curves return at
   0. **Recommendation: hard-set `DryWet` (likely 0, fully dry) and expose it as a config knob**
   rather than replicating the curves. The `AnimationCurve` assets live on the prefab; we could
   only read them off a live instance via the `PlayerVoicePlaybackControl.controls` static list,
   which requires a remote player to be present.
3. `_index` and the `Dry{n}` / `High{n}` / `ReverbFallWet{n}` / `ReverbBoostWet{n}` mixer floats
   belong to one of 12 pooled voice channels handed out from `cueStack`. Our renderer already takes
   a cue from `GlobalAudioEffects.VoiceCues`; writing these params would fight the real owner of
   that channel. **Do not write them** — the Self-Ear path should be dry, unreverbed and
   unoccluded by construction.
4. `dissonanceSampleProvider.Session` (the `isSpeaking` flag) — synthesise it from our own
   talk-burst detection (we already track burst start/end for the read-head resync).
5. `VoiceCompressor.Threshold` and `VoiceMakeupGain.s_targetARV` are process-global and are moved
   by the game's voice-volume slider; reading them is fine, **writing them is not**.
6. `AudioManager.Instance.<0x138>` (outdoorness / global voice volume) does exist for us, but it
   only feeds `_outdoornessVol` and the reverb boost, both of which the Self-Ear should skip.
7. `PlayerVoicePlaybackControl` also has a `GibberishMode` static and a blindfold `LowPass 1500 Hz
   Q 0.6` filter. Neither is part of the normal listener chain; ignore both for M2.

### Minimal faithful "Clean + remote processing" chain for the mod
```
decoded PCM -> [per sample]  g ramped from prevMakeup -> MakeupGain
                             VoiceCompressor.Process   (threshold = min(TargetARV*4, 0.6))
                             SoftClip(y, |y|)
            -> LocalVoiceProvider ring -> VoicePlayer.ProcessSamples ->
               BiquadFilters(PeakingEQ 400 Hz, Q 0.3, +30 dB, Vol 0.03, DryWet ~ 0)
            -> AudioFilterMixer clamp(-1, 1)

per frame:  MakeupGain = VoiceMakeupGain.Evaluate("<our key>", arv, isSpeaking, dt)
            where arv = mean |input sample| over the last block (pre-gain, pre-compressor)
```
`arv` must be measured **before** the gain ramp and the compressor — that is exactly what
`SamplePlaybackComponent` does (`arvSum += |_temp[k]|`), and it is what closes the AGC loop.

### Differences that will be audible if skipped
- Without `VoiceMakeupGain` the Self-Ear will not level-match the remote voices (the game targets
  ARV 0.132 and can apply up to tens of dB, slewing at 24 dB/s for the first second of a burst).
- Without the compressor + SoftClip, peaks that the remote path caps at 0.95 will hit the mixer's
  hard clamp at 1.0.
- The 400 Hz EQ is inaudible at `DryWet = 0`; it only matters if we ever want the
  distance/angle-muffled character, which the Self-Ear by definition does not have.
